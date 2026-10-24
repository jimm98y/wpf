// The multithreaded browser runtime's OS thread id, one per thread (dotnet/runtime#129900).
//
// System.Threading.Lock knows a thread by SystemNative_TryGetUInt32OSThreadId, and on the multithreaded
// WebAssembly runtime that answers the same id on every thread -- the page's, each worker, the pool's.
// A thread finding a lock held by "its own" id took it as a re-entry, so no Lock excluded another
// thread: the timer queue's lock was released by a thread that did not hold it, and the runtime aborted
// within seconds of timers running (SynchronizationLockException in TimerQueueTimer.Fire).
//
// Linked with -Wl,--wrap=SystemNative_TryGetUInt32OSThreadId (Sdk.targets), every call of it comes
// here: the thread's pthread, which is distinct for every live thread and never the "no id" value.

#include <pthread.h>
#include <stdint.h>

uint32_t __wrap_SystemNative_TryGetUInt32OSThreadId(void)
{
    return (uint32_t)(uintptr_t)pthread_self();
}
