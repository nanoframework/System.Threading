﻿//
// Copyright (c) .NET Foundation and Contributors
// Portions Copyright (c) Microsoft Corporation.  All rights reserved.
// See LICENSE file in the project root for full license information.
//

using System.Threading.Internal;

namespace System.Threading
{
    /// <summary>
    /// Provides a pool of threads that can be used to execute tasks, post work items, process asynchronous I/O, wait on behalf of other threads, and process timers.
    /// </summary>
    public static class ThreadPool
    {
        const int Workers = 12;
        const int WorkItems = 13;
        //using fixed array for performance
        static CircularQueue<ThreadWorker> workers = new CircularQueue<ThreadWorker>(Workers);
        static CircularQueue<WorkItem> pendingWorks = new CircularQueue<WorkItem>(WorkItems);

        static object mlock = new object();

        static internal void RunPendingWorkItems(ThreadWorker callingWorker)
        {
            lock (mlock)
            {
                //first find the first workitem that was requested to be run on this calling pool and start it
                for (int i = 0; i < pendingWorks.Count; i++)
                {
                    WorkItem work = pendingWorks[i]; //TODO: remove this workitem from queue
                    if (work.workerId == callingWorker.Id) //if the work must be run on this thread
                    {
                        callingWorker.Post(work.callBack, work.state);
                    }
                }
                //now run all other pending works on any available pool
                while (pendingWorks.Count > 0)
                {
                    //get an available pool
                    ThreadWorker pool = GetOrCreateFreeWorker();
                    if (pool != null)
                    {
                        do
                        {
                            WorkItem work = default;
                            if (pendingWorks.Get(ref work))
                            {
                                if (work.workerId < 0) //if the work can be run on any thread
                                {
                                    pool.Post(work.callBack, work.state);
                                }
                                else
                                {
                                    continue;
                                }
                            }
                        } while (false);
                    }
                }
            }
        }

        static ThreadWorker GetWorkerById(int id)
        {
            ThreadWorker pool = null;
            for (int i = 0; i < workers.Count; i++)
            {
                pool = workers[i];
                if (pool.Id == id)
                    return pool;
            }
            return null;
        }

        static ThreadWorker GetFreeWorker()
        {
            ThreadWorker pool = null;
            for (int i = 0; i < workers.Count; i++)
            {
                pool = workers[i];
                if (pool.IsFree)
                    return pool;
            }
            return null;
        }

        static ThreadWorker GetOrCreateFreeWorker()
        {
            ThreadWorker pool = GetFreeWorker();
            if (pool == null)
            {
                pool = new ThreadWorker();
                workers.Put(pool);
            }
            return pool;
        }
  
        /// <summary>
        /// Gets the number of thread pool threads that currently exist.
        /// </summary>
        /// <value>
        /// The number of thread pool threads that currently exist.
        /// </value>
        public static int ThreadCount => workers.Count;

        /// <summary>
        /// Gets the number of work items that have been processed so far.
        /// </summary>
        /// <value>
        /// The number of work items that have been processed so far.
        /// </value>
        public static long CompletedWorkItemCount { get; private set; }

        /// <summary>
        ///  Gets the number of work items that are currently queued to be processed.
        /// </summary>
        /// <value>
        /// The number of work items that are currently queued to be processed.
        /// </value>
        public static long PendingWorkItemCount => pendingWorks.Count;

        /// <summary>
        /// Retrieves the difference between the maximum number of thread pool threads returned by the <see cref="GetMaxThreads"/> method, and the number currently active.
        /// </summary>
        /// <param name="workerThreads">The number of available worker threads.</param>
        /// <param name="completionPortThreads">The number of available asynchronous I/O threads.</param>
        public static void GetAvailableThreads(out int workerThreads, out int completionPortThreads)
        {
            workerThreads = workers.Space;
            completionPortThreads = -1;
        }

        /// <summary>
        /// Retrieves the number of requests to the thread pool that can be active concurrently. All requests above that number remain queued until thread pool threads become available.
        /// </summary>
        /// <param name="workerThreads">The maximum number of worker threads in the thread pool.</param>
        /// <param name="completionPortThreads"> The maximum number of asynchronous I/O threads in the thread pool.</param>
        public static void GetMaxThreads(out int workerThreads, out int completionPortThreads)
        {
            workerThreads = Workers;
            completionPortThreads = -1;
        }
        //
        // Summary:
        //    
        //
        // Parameters:
        //   workerThreads:
        //     
        //
        //   completionPortThreads:
        //    

        /// <summary>
        ///  Retrieves the minimum number of threads the thread pool creates on demand, as new requests are made, before switching to an algorithm for managing thread creation and destruction.
        /// </summary>
        /// <param name="workerThreads">When this method returns, contains the minimum number of worker threads that the thread pool creates on demand.</param>
        /// <param name="completionPortThreads"> When this method returns, contains the minimum number of asynchronous I/O threads that the thread pool creates on demand.</param>
        public static void GetMinThreads(out int workerThreads, out int completionPortThreads)
        {
            workerThreads = 0;
            completionPortThreads = -1;
        }

        /// <summary>
        /// Queues a method for execution. The method executes when a thread pool thread becomes available.
        /// </summary>
        /// <param name="callBack"> A <see cref="WaitCallback"/> that represents the method to be executed.</param>
        /// <returns>true if the method is successfully queued; System.NotSupportedException is thrown if the work item could not be queued.</returns>
        public static bool QueueUserWorkItem(WaitCallback callBack)
        {
            ThreadWorker pool = GetOrCreateFreeWorker();
            if (pool != null)
            {
                pool.Post(callBack, null);
                return true;
            }
            return pendingWorks.Put(new WorkItem(callBack, null));
        }

        /// <summary>
        /// Queues a method for execution, and specifies an object containing data to be used by the method. The method executes when a thread pool thread becomes available.
        /// </summary>
        /// <param name="callBack">A <see cref="WaitCallback"/> representing the method to execute.</param>
        /// <param name="state">An object containing data to be used by the method.</param>
        /// <returns><see langword="true"/> if the method is successfully queued; <see cref="NotSupportedException"/> is thrown if the work item could not be queued.</returns>
        public static bool QueueUserWorkItem(WaitCallback callBack, object state)
        {
            ThreadWorker pool = GetOrCreateFreeWorker();
            if (pool != null)
            {
                pool.Post(callBack, state);
                return true;
            }
            return pendingWorks.Put(new WorkItem(callBack, state));
        }

        /// <summary>
        ///  Queues a method specified by an System.Action`1 delegate for execution, and provides data to be used by the method. The method executes when a thread pool thread becomes available.
        /// </summary>
        /// <typeparam name="TState">The type of elements of state.</typeparam>
        /// <param name="callBack">An Action representing the method to execute.</param>
        /// <param name="state">An object containing data to be used by the method.</param>
        /// <param name="preferLocal"><see langword="true"/> to prefer queueing the work item in a queue close to the current thread; <see langword="false"/> to prefer queueing the work item to the thread pool's shared queue.</param>
        /// <returns><see langword="true"/> if the method is successfully queued; <see cref="NotSupportedException"/> is thrown if the work item could not be queued. </returns>
        public static bool QueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            if (preferLocal)
            {
                throw new Exception("PreferLocal:true not supported");
            }

            ThreadWorker pool = GetOrCreateFreeWorker();
            
            if (pool != null)
            {
                pool.Post((_) => callBack(state), null);
               
                return true;
            }

            return pendingWorks.Put(new WorkItem((_) => callBack(state), state));
        }

        internal static bool QueueUserWorkItemOnSpecificWorker(int threadId, WaitCallback callBack, object state)
        {
            ThreadWorker pool = GetWorkerById(threadId);
          
            if (pool == null)
            {
                throw new Exception($"No such worker with id {threadId}");
            }

            if (pool.IsFree)
            {
                pool.Post(callBack, state);
                return true;
            }
            else
            {
                return pendingWorks.Put(new WorkItem((_) => callBack(state), state, threadId));
            }
        }

        internal static ThreadWorker Current => GetWorkerById(Thread.CurrentThread.ManagedThreadId);

        /// <summary>
        /// Sets the number of requests to the thread pool that can be active concurrently. All requests above that number remain queued until thread pool threads become available.
        /// </summary>
        /// <param name="workerThreads">The maximum number of worker threads in the thread pool.</param>
        /// <param name="completionPortThreads">The maximum number of asynchronous I/O threads in the thread pool.</param>
        /// <returns><see langword="true"/> if the change is successful; otherwise, <see langword="false"/>.</returns>
        public static bool SetMaxThreads(int workerThreads, int completionPortThreads)
        {
            return false;
        }

        /// <summary>
        /// Sets the minimum number of threads the thread pool creates on demand, as new requests are made, before switching to an algorithm for managing thread creation and destruction.
        /// </summary>
        /// <param name="workerThreads">The minimum number of worker threads that the thread pool creates on demand.</param>
        /// <param name="completionPortThreads">The minimum number of asynchronous I/O threads that the thread pool creates on demand.</param>
        /// <returns><see langword="true"/> if the change is successful; otherwise, <see langword="false"/>.</returns>
        public static bool SetMinThreads(int workerThreads, int completionPortThreads)
        {
            return false;
        }
    }
}