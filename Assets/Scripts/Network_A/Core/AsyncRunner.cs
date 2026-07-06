using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.Core
{
    public static class AsyncRunner
    {
        public static async void Run(Task task)
        {
            try { await task; }
            catch (Exception ex) { Debug.LogError(ex); }
        }
    }
}
