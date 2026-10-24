using System;
using System.Threading.Tasks;

namespace Network_A.Core
{
    public interface IRequestItem
    {
        Task Execute();
        void Cancel();
    }

    public sealed class RequestItem<T> : IRequestItem
    {
        public Func<Task<ApiResult<T>>> Action;
        public Action CancelAction;
        public TaskCompletionSource<ApiResult<T>> Tcs;

        //* Executes the queued request and resolves the awaiting caller.
        public async Task Execute()
        {
            try { Tcs.TrySetResult(await Action()); }
            catch (OperationCanceledException) { Tcs.TrySetCanceled(); }
            catch (Exception ex) { Tcs.TrySetException(ex); }
            finally { CancelAction = null; }
        }

        //* درخواست صف‌شده‌ای را که دیگر نباید اجرا شود لغو می‌کند و انتظار فراخواننده را پایان می‌دهد.
        public void Cancel()
        {
            try { if (CancelAction != null) CancelAction(); }
            catch (Exception) { }
            finally
            {
                CancelAction = null;
                Tcs.TrySetCanceled();
            }
        }
    }
}
