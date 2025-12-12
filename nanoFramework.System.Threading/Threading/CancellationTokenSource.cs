// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;

namespace System.Threading
{
    /// <summary>
    /// Signals to a <see cref="CancellationToken"/> that it should be canceled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="CancellationTokenSource"/> is used to create <see cref="CancellationToken"/> instances
    /// (via the <see cref="Token"/> property) that can be passed to operations that support cancellation.
    /// Cancellation can then be requested for those operations by calling <see cref="Cancel()"/> on the source.
    /// </para>
    /// <para>
    /// All members of this class, except <see cref="Dispose()"/>, are thread-safe and may be used concurrently from multiple threads.
    /// </para>
    /// </remarks>
    public class CancellationTokenSource : IDisposable
    {
        // Static sources that can be used as the backing source for "fixed" CancellationTokens that never change state.
        private static readonly CancellationTokenSource _staticSourceSet = new CancellationTokenSource(true);
        private static readonly CancellationTokenSource _staticSourceNotCancelable = new CancellationTokenSource(false);

        // Lazily initialized if required.
        private ManualResetEvent _kernelEvent;
        private ArrayList _registeredCallbacksLists;
        private CancellationTokenRegistration[] _linkingRegistrations;
        private Action _executingCallback;

        // Legal values for state
        private const int CannotBeCancelled = 0;
        private const int NotCancelled = 1;
        private const int Notifying = 2;
        private const int NotifyingCompleted = 3;

        private int _state;
        private bool _disposed;

        // Timer for cancellation after specific amount of time.
        private static readonly TimerCallback _timerCallback = new TimerCallback(TimerCallbackLogic);

        /// <summary>
        /// The ID of the thread currently executing the main body of <see cref="Cancel()"/>.
        /// </summary>
        /// <remarks>
        /// This is used to determine whether a call to <see cref="CancellationTokenRegistration.Dispose"/> is running within
        /// a cancellation callback. It is updated as execution moves between the thread invoking <see cref="Cancel()"/>
        /// and any contexts used to run the callbacks.
        /// </remarks>
        private int _threadIDExecutingCallbacks = -1;

        // Provided for CancelAfter and timer-related constructors.
        private Timer _timer;

        /// <summary>
        /// Gets whether cancellation has been requested for this <see cref="CancellationTokenSource"/>.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if cancellation has been requested for this <see cref="CancellationTokenSource"/>;
        /// otherwise, <see langword="false"/>.
        /// </value>
        /// <remarks>
        /// <para>
        /// This property indicates whether cancellation has been requested for this token source, for example
        /// due to a call to <see cref="Cancel()"/>.
        /// </para>
        /// <para>
        /// If this property returns <see langword="true"/>, it only indicates that cancellation has been requested.
        /// It does not guarantee that all registered callbacks have finished executing, nor that cancellation
        /// has finished propagating to every registered handler. Additional synchronization may be required,
        /// particularly when related objects are canceled concurrently.
        /// </para>
        /// </remarks>
        public bool IsCancellationRequested => _state >= Notifying;

        /// <summary>
        /// Gets a value that indicates whether cancellation processing has completed.
        /// </summary>
        internal bool IsCancellationCompleted => _state == NotifyingCompleted;

        /// <summary>
        /// Gets a value that indicates whether this <see cref="CancellationTokenSource"/> has been disposed.
        /// </summary>
        internal bool IsDisposed => _disposed;

        /// <summary>
        /// Gets or sets the ID of the thread that is currently running callbacks.
        /// </summary>
        internal int ThreadIDExecutingCallbacks
        {
            set { _threadIDExecutingCallbacks = value; }
            get { return _threadIDExecutingCallbacks; }
        }

        /// <summary>
        /// Gets the <see cref="CancellationToken"/> associated with this <see cref="CancellationTokenSource"/>.
        /// </summary>
        /// <value>The <see cref="CancellationToken"/> associated with this <see cref="CancellationTokenSource"/>.</value>
        /// <exception cref="ObjectDisposedException">The token source has been disposed.</exception>
        public CancellationToken Token
        {
            get
            {
                ThrowIfDisposed();

                return new CancellationToken(this);
            }
        }

        internal bool CanBeCanceled => _state != CannotBeCancelled;

        internal WaitHandle WaitHandle
        {
            get
            {
                ThrowIfDisposed();

                // Fast path if already allocated.
                if (_kernelEvent != null)
                {
                    return _kernelEvent;
                }

                _kernelEvent = new ManualResetEvent(false);

                // There is a race between checking IsCancellationRequested and setting the event.
                // However, at this point, the kernel object definitely exists and the cases are:
                // 1. if IsCancellationRequested == true, then we will call Set()
                // 2. if IsCancellationRequested == false, then NotifyCancellation will see that the event exists and will call Set().
                if (IsCancellationRequested)
                {
                    _kernelEvent.Set();
                }

                return _kernelEvent;
            }
        }

        /// <summary>
        /// Throws an <see cref="ObjectDisposedException"/> if the source has been disposed.
        /// </summary>
        internal void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException();
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CancellationTokenSource"/> class that will be canceled after the specified number of milliseconds.
        /// </summary>
        /// <param name="millisecondsDelay">The number of milliseconds to wait before canceling this <see cref="CancellationTokenSource"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="millisecondsDelay"/> is less than -1.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The countdown for <paramref name="millisecondsDelay"/> starts during the call to the constructor.
        /// When the delay expires, the constructed <see cref="CancellationTokenSource"/> is canceled if it has
        /// not already been canceled.
        /// </para>
        /// <para>
        /// Subsequent calls to <see cref="CancelAfter(int)"/> will reset the delay for the constructed
        /// <see cref="CancellationTokenSource"/>, provided it has not already been canceled.
        /// </para>
        /// </remarks>
        public CancellationTokenSource(int millisecondsDelay)
        {
            if (millisecondsDelay < -1)
            {
                throw new ArgumentOutOfRangeException();
            }

            InitializeWithTimer(millisecondsDelay);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CancellationTokenSource"/> class that will be canceled after the specified time span.
        /// </summary>
        /// <param name="delay">The time span to wait before canceling this <see cref="CancellationTokenSource"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="delay"/> is less than -1 milliseconds or greater than <see cref="int.MaxValue"/> milliseconds.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The countdown for the <paramref name="delay"/> starts during the call to the constructor. When the delay expires,
        /// the constructed <see cref="CancellationTokenSource"/> is canceled if it has not already been canceled.
        /// </para>
        /// <para>
        /// Subsequent calls to <see cref="CancelAfter(TimeSpan)"/> will reset the delay for the constructed
        /// <see cref="CancellationTokenSource"/>, provided it has not already been canceled.
        /// </para>
        /// </remarks>
        public CancellationTokenSource(TimeSpan delay)
        {
            long totalMilliseconds = (long)delay.TotalMilliseconds;

            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException();
            }

            InitializeWithTimer((int)totalMilliseconds);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CancellationTokenSource"/> class.
        /// </summary>
        public CancellationTokenSource() => _state = NotCancelled;

        // Private constructors for static sources.
        // set == false ==> cannot be canceled.
        // set == true ==> already canceled.
        private CancellationTokenSource(bool set) => _state = set ? NotifyingCompleted : CannotBeCancelled;

        // Common initialization logic when constructing a CTS with a delay parameter.
        private void InitializeWithTimer(int millisecondsDelay)
        {
            _state = NotCancelled;
            _timer = new Timer(_timerCallback, this, millisecondsDelay, -1);
        }

        /// <summary>
        /// Communicates a request for cancellation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The associated <see cref="CancellationToken"/> will be notified of the cancellation and will transition to a state where
        /// <see cref="CancellationToken.IsCancellationRequested"/> returns <see langword="true"/>.
        /// Any callbacks or cancelable operations registered with the <see cref="CancellationToken"/> will be executed.
        /// </para>
        /// <para>
        /// Cancelable operations and callbacks registered with the token should not throw exceptions.
        /// If an exception is thrown, it will be observed by the overload of <see cref="Cancel(bool)"/> with
        /// <paramref name="throwOnFirstException"/> set to <see langword="true"/>.
        /// </para>
        /// </remarks>
        public void Cancel() => Cancel(false);

        /// <summary>
        /// Communicates a request for cancellation.
        /// </summary>
        /// <param name="throwOnFirstException">Specifies whether exceptions should immediately propagate.</param>
        /// <remarks>
        /// <para>
        /// The associated <see cref="CancellationToken"/> will be notified of the cancellation and will transition to a state where
        /// <see cref="CancellationToken.IsCancellationRequested"/> returns <see langword="true"/>.
        /// Any callbacks or cancelable operations registered with the <see cref="CancellationToken"/> will be executed.
        /// </para>
        /// <para>
        /// Cancelable operations and callbacks registered with the token should not throw exceptions.
        /// If <paramref name="throwOnFirstException"/> is <see langword="true"/>, an exception will immediately propagate out of the
        /// call to <see cref="Cancel(bool)"/>, preventing the remaining callbacks and cancelable operations from being processed.
        /// If <paramref name="throwOnFirstException"/> is <see langword="false"/>, this overload may aggregate any
        /// exceptions thrown into an <see cref="AggregateException"/>, such that one callback throwing an exception
        /// does not prevent other registered callbacks from being executed.
        /// </para>
        /// </remarks>
        /// <exception cref="AggregateException">An aggregate exception containing all the exceptions thrown
        /// by the registered callbacks on the associated <see cref="CancellationToken"/>.</exception>
        /// <exception cref="ObjectDisposedException">This <see cref="CancellationTokenSource"/> has been disposed.</exception>
        public void Cancel(bool throwOnFirstException)
        {
            ThrowIfDisposed();

            NotifyCancellation(throwOnFirstException);
        }

        /// <summary>
        /// Schedules a call to <see cref="Cancel()"/> on this <see cref="CancellationTokenSource"/>.
        /// </summary>
        /// <param name="delay">The time span to wait before canceling this <see cref="CancellationTokenSource"/>.</param>
        /// <exception cref="ObjectDisposedException">This <see cref="CancellationTokenSource"/> has been disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="delay"/> is less than -1 milliseconds or greater than <see cref="int.MaxValue"/> milliseconds.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The countdown for the <paramref name="delay"/> starts during this call. When the delay expires,
        /// this <see cref="CancellationTokenSource"/> is canceled if it has not been canceled already.
        /// </para>
        /// <para>
        /// Subsequent calls to <see cref="CancelAfter(TimeSpan)"/> will reset the delay for this
        /// <see cref="CancellationTokenSource"/>, provided it has not already been canceled.
        /// </para>
        /// </remarks>
        public void CancelAfter(TimeSpan delay)
        {
            long totalMilliseconds = (long)delay.TotalMilliseconds;

            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException();
            }

            CancelAfter((int)totalMilliseconds);
        }

        /// <summary>
        /// Schedules a call to <see cref="Cancel()"/> on this <see cref="CancellationTokenSource"/>.
        /// </summary>
        /// <param name="millisecondsDelay">The number of milliseconds to wait before canceling this <see cref="CancellationTokenSource"/>.</param>
        /// <exception cref="ObjectDisposedException">This <see cref="CancellationTokenSource"/> has been disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="millisecondsDelay"/> is less than -1.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The countdown for <paramref name="millisecondsDelay"/> starts during this call. When the delay expires,
        /// this <see cref="CancellationTokenSource"/> is canceled if it has not been canceled already.
        /// </para>
        /// <para>
        /// Subsequent calls to <see cref="CancelAfter(int)"/> will reset the delay for this
        /// <see cref="CancellationTokenSource"/>, provided it has not been canceled already.
        /// </para>
        /// </remarks>
        public void CancelAfter(int millisecondsDelay)
        {
            ThrowIfDisposed();

            if (millisecondsDelay < -1)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (IsCancellationRequested)
            {
                return;
            }

            // There is a race condition here as a Cancel could occur between the check of
            // IsCancellationRequested and the creation of the timer. This is benign; in the
            // worst case, a timer will be created that has no effect when it expires.

            // Also, if Dispose() is called right here (after ThrowIfDisposed(), before timer
            // creation), it would result in a leaked Timer object (at least until the timer
            // expired and disposed itself). But this would be considered bad behavior, as
            // Dispose() is not thread-safe and should not be called concurrently with CancelAfter().

            // Lazily initialize the timer in a thread-safe fashion.
            // Initially set to "never go off" because we don't want to take a
            // chance on a timer "losing" the initialization race and then
            // canceling the token before it (the timer) can be disposed.
            _timer ??= new Timer(_timerCallback, this, -1, -1);

            // It is possible that _timer has already been disposed, so we must do
            // the following in a try/catch block.
            try
            {
                _timer.Change(millisecondsDelay, -1);
            }
            catch (ObjectDisposedException)
            {
                // Swallow the exception. There is no other way to tell that
                // the timer has been disposed, and even if there were, there
                // would not be a good way to deal with the observe/dispose
                // race condition.
            }
        }

        // Common logic for a timer delegate.
        private static void TimerCallbackLogic(object obj)
        {
            CancellationTokenSource cts = (CancellationTokenSource)obj;

            // Cancel the source; handle a race condition with cts.Dispose().
            if (!cts.IsDisposed)
            {
                // There is a small window for a race condition where a cts.Dispose can sneak
                // in right here. Wrap cts.Cancel() in a try/catch to guard against this.
                try
                {
                    cts.Cancel(); // will take care of disposing of _timer
                }
                catch (ObjectDisposedException)
                {
                    // If the ODE was not due to the target cts being disposed, then propagate the ODE.
                    if (!cts.IsDisposed)
                    {
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Releases the resources used by this <see cref="CancellationTokenSource"/>.
        /// </summary>
        /// <remarks>
        /// This method is not guaranteed to be thread-safe when called concurrently with other calls to <see cref="Dispose()"/>.
        /// </remarks>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="CancellationTokenSource"/> class and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">
        /// <see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            // There is nothing to do if disposing == false because the CancellationTokenSource holds no unmanaged resources.

            if (disposing)
            {
                // NOTE: We specifically tolerate that a callback can be deregistered
                // after the CTS has been disposed and/or concurrently with Dispose().
                // This is safe without locks because the registration's Dispose() only
                // mutates the callbacks list and then reads from properties of the CTS that are not
                // invalidated by Dispose().
                //
                // We also tolerate that a callback can be registered after the CTS has been
                // disposed. This is safe without locks because InternalRegister is tolerant
                // of _registeredCallbacksLists becoming null during its execution. However,
                // we run the acceptable risk of _registeredCallbacksLists getting reinitialized
                // to non-null if there is a race between Dispose and Register, in which case this
                // instance may unnecessarily hold onto a registered callback.

                if (_disposed)
                {
                    return;
                }

                _timer?.Dispose();

                CancellationTokenRegistration[] linkingRegistrations = _linkingRegistrations;

                if (linkingRegistrations != null)
                {
                    // free for GC once we're done enumerating
                    _linkingRegistrations = null;

                    for (int i = 0; i < linkingRegistrations.Length; i++)
                    {
                        linkingRegistrations[i].Dispose();
                    }
                }

                // Registered callbacks are now either complete or will never run, due to guarantees made by CancellationTokenRegistration.Dispose().
                // So we can now perform main disposal work without risk of linking callbacks trying to use this CTS.
                // free for GC.
                _registeredCallbacksLists = null;

                if (_kernelEvent != null)
                {
                    // _kernelEvent would be closed/disposed here in a full framework implementation.
                    // free for GC.
                    _kernelEvent = null;
                }

                _disposed = true;
            }
        }

        internal static CancellationTokenSource InternalGetStaticSource(bool set) => set ? _staticSourceSet : _staticSourceNotCancelable;

        private void NotifyCancellation(bool throwOnFirstException)
        {
            // Fast-path test to check if Notify has been called previously.
            if (IsCancellationRequested)
            {
                return;
            }

            // If we're the first to signal cancellation, do the main extra work.
            if (Interlocked.CompareExchange(ref _state, Notifying, NotCancelled) == NotCancelled)
            {
                // Dispose of the timer, if any.
                Timer timer = _timer;

                timer?.Dispose();

                // Record the thread ID being used for running the callbacks.
                ThreadIDExecutingCallbacks = Thread.CurrentThread.ManagedThreadId;

                // If the kernel event is null at this point, it will be set during lazy construction.
                // Update the MRE value.
                _kernelEvent?.Set();

                // - Late enlisters to the canceled event will have their callbacks called immediately in the Register() methods.
                // - Callbacks are not called inside a lock.
                // - After transition, no more delegates will be added to the
                // list of handlers, and hence it can be consumed and cleared at leisure by ExecuteCallbackHandlers.
                ExecuteCallbackHandlers(throwOnFirstException);
            }
        }

        internal CancellationTokenRegistration InternalRegister(Action callback)
        {
            if (!IsCancellationRequested)
            {
                if (_disposed)
                {
                    return new CancellationTokenRegistration();
                }

                _registeredCallbacksLists = _registeredCallbacksLists ?? new ArrayList();
                _registeredCallbacksLists.Add(callback);
                CancellationTokenRegistration registration = new CancellationTokenRegistration(callback, this);

                if (!IsCancellationRequested)
                {
                    return registration;
                }

                bool deregisterOccurred = registration.TryDeregister();

                if (!deregisterOccurred)
                {
                    // The thread that is running Cancel() snagged our callback for execution.
                    // So we don't need to run it, but we do return the registration so that
                    // CancellationTokenRegistration.Dispose() will wait for callback completion.
                    return registration;
                }
            }

            callback?.Invoke();

            return new CancellationTokenRegistration();
        }

        internal void Unregister(Action callback)
        {
            if (!IsCancellationRequested)
            {
                _registeredCallbacksLists?.Remove(callback);
            }
        }

        private void ExecuteCallbackHandlers(bool throwOnFirstException)
        {
            // Design decision: call the delegates in LIFO order so that callbacks fire 'deepest first'.
            // This is intended to help with nesting scenarios so that child enlisters cancel before their parents.
            bool exception = false;

            // If there are no callbacks to run, we can safely exit. Any races to lazy initialize the list
            // will see IsCancellationRequested and will then run the callback themselves.
            if (_registeredCallbacksLists == null)
            {
                Interlocked.Exchange(ref _state, NotifyingCompleted);
                return;
            }

            try
            {
                ArrayList toExecute = new ArrayList();
                // Copy the current callbacks into a local list.
                foreach (var callback in _registeredCallbacksLists)
                {
                    toExecute.Add(callback);
                }

                for (int index = toExecute.Count - 1; index >= 0; index--)
                {
                    _executingCallback = (Action)toExecute[index];

                    try
                    {
                        Unregister(_executingCallback);
                        _executingCallback.Invoke();
                    }
                    catch
                    {
                        exception = true;

                        if (throwOnFirstException)
                        {
                            throw;
                        }
                    }
                }
            }
            finally
            {
                _state = NotifyingCompleted;
                _executingCallback = null;
            }

            if (exception && !throwOnFirstException)
            {
                // In this nanoFramework implementation, we do not currently aggregate and rethrow exceptions.
                // The flag is kept for API similarity.
            }
        }

        /// <summary>
        /// Waits for the specified callback to complete (or, more specifically, to stop running).
        /// </summary>
        /// <param name="callbackInfo">The callback to wait for.</param>
        /// <remarks>
        /// It is valid to call this method if the callback has already finished. Calling this method
        /// before the target callback has been selected for execution is an error.
        /// </remarks>
        internal void WaitForCallbackToComplete(Action callbackInfo)
        {
            SpinWait sw = new SpinWait();

            while (_executingCallback == callbackInfo)
            {
                // Spin as we assume callback execution is fast and that this situation is rare.
                sw.SpinOnce();
            }
        }
    }
}
