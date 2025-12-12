// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading
{
    /// <summary>
    /// Propagates notification that operations should be canceled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="CancellationToken"/> may be created directly in an unchangeable canceled or non-canceled state
    /// using the <see cref="CancellationToken"/> constructors. However, to have a <see cref="CancellationToken"/> that can change
    /// from a non-canceled to a canceled state, a <see cref="CancellationTokenSource"/> must be used.
    /// <see cref="CancellationTokenSource"/> exposes the associated <see cref="CancellationToken"/> that may be canceled by the source through its
    /// <see cref="CancellationTokenSource.Token"/> property.
    /// </para>
    /// <para>
    /// Once canceled, a token may not transition to a non-canceled state, and a token whose
    /// <see cref="CanBeCanceled"/> is <see langword="false"/> will never change to one that can be canceled.
    /// </para>
    /// <para>
    /// All members of this struct are thread-safe and may be used concurrently from multiple threads.
    /// </para>
    /// </remarks>
    public struct CancellationToken
    {
        // The backing CancellationTokenSource.
        // If null, it implicitly represents the same thing as new CancellationToken(false).
        // When required, it will be instantiated to reflect this.
        private CancellationTokenSource _source;

        /// <summary>
        /// Gets an empty <see cref="CancellationToken"/> value.
        /// </summary>
        /// <remarks>
        /// The <see cref="CancellationToken"/> value returned by this property is non-cancelable.
        /// </remarks>
        public static CancellationToken None
        {
            get { return default; }
        }

        /// <summary>
        /// Gets a value indicating whether cancellation has been requested for this token.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if cancellation has been requested for this token; otherwise, <see langword="false"/>.
        /// </value>
        /// <remarks>
        /// <para>
        /// This property indicates whether cancellation has been requested for this token,
        /// either through the token initially being constructed in a canceled state, or through
        /// calling <see cref="CancellationTokenSource.Cancel()"/> on the token's associated <see cref="CancellationTokenSource"/>.
        /// </para>
        /// <para>
        /// If this property is <see langword="true"/>, it only guarantees that cancellation has been requested.
        /// It does not guarantee that every registered handler has finished executing, nor that cancellation
        /// requests have finished propagating to all registered handlers. Additional synchronization may be required,
        /// particularly in situations where related objects are being canceled concurrently.
        /// </para>
        /// </remarks>
        public bool IsCancellationRequested => _source != null && _source.IsCancellationRequested;

        /// <summary>
        /// Gets a value indicating whether this token is capable of being in the canceled state.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if this token is capable of being in the canceled state; otherwise, <see langword="false"/>.
        /// </value>
        /// <remarks>
        /// If <see cref="CanBeCanceled"/> returns <see langword="false"/>, it is guaranteed that the token will never transition
        /// into a canceled state, meaning that <see cref="IsCancellationRequested"/> will never return <see langword="true"/>.
        /// </remarks>
        public bool CanBeCanceled => _source != null && _source.CanBeCanceled;

        /// <summary>
        /// Gets a <see cref="WaitHandle"/> that is signaled when the token is canceled.
        /// </summary>
        /// <remarks>
        /// Accessing this property causes a <see cref="WaitHandle"/> to be instantiated. It is preferable to only use
        /// this property when necessary, and to then dispose the associated <see cref="CancellationTokenSource"/> instance at
        /// the earliest opportunity (disposing the source will dispose this handle). The handle should not be closed or disposed directly.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The associated <see cref="CancellationTokenSource"/> has been disposed.</exception>
        public WaitHandle WaitHandle
        {
            get
            {
                _source ??= CancellationTokenSource.InternalGetStaticSource(false);

                return _source.WaitHandle;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CancellationToken"/> struct.
        /// </summary>
        /// <param name="source">The <see cref="CancellationTokenSource"/> that will control this token.</param>
        internal CancellationToken(CancellationTokenSource source) => _source = source;

        /// <summary>
        /// Initializes a new instance of the <see cref="CancellationToken"/> struct.
        /// </summary>
        /// <param name="canceled">The canceled state for the token.</param>
        /// <remarks>
        /// Tokens created with this constructor will remain in the canceled state specified
        /// by the <paramref name="canceled"/> parameter. If <paramref name="canceled"/> is <see langword="false"/>,
        /// both <see cref="CanBeCanceled"/> and <see cref="IsCancellationRequested"/> will be <see langword="false"/>.
        /// If <paramref name="canceled"/> is <see langword="true"/>, both <see cref="CanBeCanceled"/> and <see cref="IsCancellationRequested"/> will be <see langword="true"/>.
        /// </remarks>
        public CancellationToken(bool canceled) : this()
        {
            if (canceled)
            {
                _source = CancellationTokenSource.InternalGetStaticSource(canceled);
            }
        }

        /// <summary>
        /// Registers a delegate that will be called when this <see cref="CancellationToken"/> is canceled.
        /// </summary>
        /// <param name="callback">The delegate to be executed when the <see cref="CancellationToken"/> is canceled.</param>
        /// <returns>
        /// The <see cref="CancellationTokenRegistration"/> instance that can be used to deregister the callback.
        /// </returns>
        /// <remarks>
        /// <para>
        /// If this token is already in the canceled state, the delegate will be run immediately and synchronously.
        /// Any exception the delegate generates will be propagated out of this method call.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
        public CancellationTokenRegistration Register(Action callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException();
            }

            if (CanBeCanceled == false)
            {
                // Nothing to do for tokens that can never reach the canceled state. Give them a dummy registration.
                return new CancellationTokenRegistration();
            }

            // Register the callback with the source.
            return _source.InternalRegister(callback);
        }

        /// <summary>
        /// Determines whether the current <see cref="CancellationToken"/> instance is equal to the specified token.
        /// </summary>
        /// <param name="other">The other <see cref="CancellationToken"/> to which to compare this instance.</param>
        /// <returns>
        /// <see langword="true"/> if the instances are equal; otherwise, <see langword="false"/>.
        /// Two tokens are equal if they are associated with the same <see cref="CancellationTokenSource"/> or if they were
        /// both constructed from public <see cref="CancellationToken"/> constructors and their
        /// <see cref="IsCancellationRequested"/> values are equal.
        /// </returns>
        public bool Equals(CancellationToken other)
        {
            // If both sources are null, then both tokens represent the Empty token.
            if (_source == null && other._source == null)
            {
                return true;
            }

            // One is null but the other has inflated the default source;
            // these are only equal if the inflated one is the staticSource(false).
            if (_source == null)
            {
                return other._source == CancellationTokenSource.InternalGetStaticSource(false);
            }

            if (other._source == null)
            {
                return _source == CancellationTokenSource.InternalGetStaticSource(false);
            }

            // General case: check if the sources are identical.
            return _source == other._source;
        }

        /// <summary>
        /// Determines whether the current <see cref="CancellationToken"/> instance is equal to the specified <see cref="object"/>.
        /// </summary>
        /// <param name="other">The other object to which to compare this instance.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="other"/> is a <see cref="CancellationToken"/> and the two instances are equal;
        /// otherwise, <see langword="false"/>.
        /// Two tokens are equal if they are associated with the same <see cref="CancellationTokenSource"/> or if they were both
        /// constructed from public <see cref="CancellationToken"/> constructors and their
        /// <see cref="IsCancellationRequested"/> values are equal.
        /// </returns>
        public override bool Equals(object other)
        {
            if (other is CancellationToken token)
            {
                return Equals(token);
            }

            return false;
        }

        /// <summary>
        /// Serves as a hash function for a <see cref="CancellationToken"/>.
        /// </summary>
        /// <returns>A hash code for the current <see cref="CancellationToken"/> instance.</returns>
        public override int GetHashCode()
        {
            if (_source == null)
            {
                // Link to the common source so that we have a source to interrogate.
                return CancellationTokenSource.InternalGetStaticSource(false).GetHashCode();
            }

            return _source.GetHashCode();
        }

        /// <summary>
        /// Throws an <see cref="OperationCanceledException"/> if this token has had cancellation requested.
        /// </summary>
        /// <remarks>
        /// This method provides functionality equivalent to:
        /// <code>
        /// if (token.IsCancellationRequested)
        /// {
        /// throw new OperationCanceledException();
        /// }
        /// </code>
        /// </remarks>
        /// <exception cref="OperationCanceledException">The token has had cancellation requested.</exception>
        public void ThrowIfCancellationRequested()
        {
            if (IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
        }
    }
}
