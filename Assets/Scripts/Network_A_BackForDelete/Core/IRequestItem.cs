using System;
using System.Threading.Tasks;

namespace Network_A.Core
{
    public interface IRequestItem
    {
        Task Execute();
    }

    public sealed class RequestItem<T> : IRequestItem
    {
        public Func<Task<ApiResult<T>>> Action;
        public TaskCompletionSource<ApiResult<T>> Tcs;

        //* Executes the queued request and resolves the awaiting caller.
        public async Task Execute()
        {
            try { Tcs.TrySetResult(await Action()); }
            catch (Exception ex) { Tcs.TrySetException(ex); }
        }
    }
}
