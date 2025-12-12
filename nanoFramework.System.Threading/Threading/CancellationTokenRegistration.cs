// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading
{
    /// <summary>
    /// Represents a callback delegate that has been registered with a <see cref="CancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// To unregister a callback, dispose the corresponding <see cref="CancellationTokenRegistration"/> instance.
    /// </remarks>
    public readonly struct CancellationTokenRegistration : IDisposable
    {
        private readonly Action _callback;
        private readonly CancellationTokenSource _source;

        internal CancellationTokenRegistration(Action callbackInfo, CancellationTokenSource registrationInfo)
        {
            _callback = callbackInfo;
            _source = registrationInfo;
        }

        /// <summary>
        /// Attempts to deregister the callback.
        /// </summary>
        /// <remarks>
        /// If the callback is already executing, deregistration may fail.
        /// </remarks>
        /// <returns>
        /// <see langword="true"/> if the callback was found and deregistered; otherwise, <see langword="false"/>.
        /// </returns>
        internal bool TryDeregister()
        {
            // Try to remove the callback info from the source.
            // It is possible the callback info is missing (removed for execution or removed by someone else).
            try
            {
                _source.Unregister(_callback);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Disposes of the registration and unregisters the target callback from the associated <see cref="CancellationToken"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If the target callback is currently executing, this method will wait until it completes, except
        /// in the degenerate cases where a callback method deregisters itself.
        /// </para>
        /// </remarks>
        public void Dispose()
        {
            // Remove the entry from the source.
            bool deregisterOccured = TryDeregister();

            // We guarantee that we will not return if the callback is being executed (assuming we are not
            // currently called by the callback itself).
            //
            // We achieve this by the following rules:
            // 1. If we are called in the context of an executing callback, there is no need to wait
            // (determined by tracking callback-executor thread ID).
            // 2. If deregistration failed and we are on a different thread, the callback may be running
            // under control of CancellationTokenSource.Cancel(), so we poll until the executing
            // callback is no longer this registration's callback.

            var callbackInfo = _callback;

            if (callbackInfo != null)
            {
                var tokenSource = _source;

                if (tokenSource.IsCancellationRequested && // running callbacks has commenced
                    !tokenSource.IsCancellationCompleted && // running callbacks hasn't finished
                    !deregisterOccured) // deregistration failed (i.e. the callback is missing from the list)
                {
                    // Callback execution is in progress, the executing thread is different to us and has taken
                    // the callback for execution, so observe and wait until this target callback is no longer
                    // the executing callback.
                    tokenSource.WaitForCallbackToComplete(_callback);
                }
            }
        }

        /// <summary>
        /// Determines whether two <see cref="CancellationTokenRegistration"/> instances are equal.
        /// </summary>
        /// <param name="left">The first instance.</param>
        /// <param name="right">The second instance.</param>
        /// <returns>
        /// <see langword="true"/> if the instances are equal; otherwise, <see langword="false"/>.
        /// </returns>
        public static bool operator ==(CancellationTokenRegistration left, CancellationTokenRegistration right) => left.Equals(right);

        /// <summary>
        /// Determines whether two <see cref="CancellationTokenRegistration"/> instances are not equal.
        /// </summary>
        /// <param name="left">The first instance.</param>
        /// <param name="right">The second instance.</param>
        /// <returns>
        /// <see langword="true"/> if the instances are not equal; otherwise, <see langword="false"/>.
        /// </returns>
        public static bool operator !=(CancellationTokenRegistration left, CancellationTokenRegistration right) => !left.Equals(right);

        /// <summary>
        /// Determines whether the current <see cref="CancellationTokenRegistration"/> instance is equal to the specified <see cref="object"/>.
        /// </summary>
        /// <param name="obj">The other object to which to compare this instance.</param>
        /// <returns>
        /// <see langword="true"/> if both this and <paramref name="obj"/> are equal; otherwise, <see langword="false"/>.
        /// Two <see cref="CancellationTokenRegistration"/> instances are equal if they both refer to the output of a single
        /// call to the same <see cref="CancellationToken.Register(System.Action)"/> method.
        /// </returns>
        public override bool Equals(object obj) => (obj is CancellationTokenRegistration registration) && Equals(registration);

        /// <summary>
        /// Determines whether the current <see cref="CancellationTokenRegistration"/> instance is equal to the specified instance.
        /// </summary>
        /// <param name="other">The other <see cref="CancellationTokenRegistration"/> to which to compare this instance.</param>
        /// <returns>
        /// <see langword="true"/> if both this and <paramref name="other"/> are equal; otherwise, <see langword="false"/>.
        /// Two <see cref="CancellationTokenRegistration"/> instances are equal if they both refer to the output of a single
        /// call to the same <see cref="CancellationToken.Register(System.Action)"/> method.
        /// </returns>
        public bool Equals(CancellationTokenRegistration other) => _callback == other._callback && _source == other._source;

        /// <summary>
        /// Serves as a hash function for a <see cref="CancellationTokenRegistration"/>.
        /// </summary>
        /// <returns>A hash code for the current <see cref="CancellationTokenRegistration"/> instance.</returns>
        public override int GetHashCode()
        {
            return _source != null ? _source.GetHashCode() ^ _callback.GetHashCode() : _callback.GetHashCode();
        }
    }
}
