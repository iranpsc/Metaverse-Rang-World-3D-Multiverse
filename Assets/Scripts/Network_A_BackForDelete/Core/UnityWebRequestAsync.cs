using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Network_A.Core
{
    public static class UnityWebRequestAsync
    {
        //* Sends a UnityWebRequest and exposes it as an awaitable Task.
        public static Task<UnityWebRequest> SendAsync(UnityWebRequest request, CancellationToken ct = default(CancellationToken))
        {
            var tcs = new TaskCompletionSource<UnityWebRequest>();
            CoroutineRunner_A.Run(Send(request, tcs, ct));
            return tcs.Task;
        }

        //* Coroutine body that drives the UnityWebRequest until completion or cancellation.
        private static IEnumerator Send(UnityWebRequest request, TaskCompletionSource<UnityWebRequest> tcs, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                request.Abort();
                tcs.TrySetCanceled();
                yield break;
            }

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                if (ct.IsCancellationRequested)
                {
                    request.Abort();
                    tcs.TrySetCanceled();
                    yield break;
                }

                yield return null;
            }

            if (ct.IsCancellationRequested)
            {
                request.Abort();
                tcs.TrySetCanceled();
                yield break;
            }

            tcs.TrySetResult(request);
        }
    }
}
