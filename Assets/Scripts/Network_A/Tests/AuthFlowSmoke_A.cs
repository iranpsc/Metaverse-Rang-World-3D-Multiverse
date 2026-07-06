using Network_A.Auth;
using Network_A.Core;
using UnityEngine;

namespace Network_A.Tests
{
    public class AuthFlowSmoke_A : MonoBehaviour
    {
        public string User = "test_user_unity_flow";
        public string Pass = "1234";

        //* Runs a compact auth flow test from the Inspector context menu.
        [ContextMenu("Run Auth Flow Smoke")]
        public async void RunFromInspector()
        {
            if (AuthManager.Instance == null)
            {
                Debug.LogError("[AuthFlowSmoke_A] AuthManager is missing in scene.");
                return;
            }

            SecureTokenStorage.ClearTokens();

            ApiResult<AuthResponseDto> register = await AuthManager.Instance.RegisterAsync(User, Pass);
            Debug.Log("[AuthFlowSmoke_A] Register=" + register.IsSuccess + " Status=" + register.StatusCode + " Error=" + register.ErrorMessage);

            ApiResult<AuthResponseDto> login = await AuthManager.Instance.LoginAsync(User, Pass);
            Debug.Log("[AuthFlowSmoke_A] Login=" + login.IsSuccess + " Status=" + login.StatusCode + " Error=" + login.ErrorMessage);

            ApiResult<GetUserDataResponseDto> me = await AuthManager.Instance.GetUserDataAsync();
            Debug.Log("[AuthFlowSmoke_A] Me=" + me.IsSuccess + " User=" + (me.Data != null && me.Data.user != null ? me.Data.user.emailOrUsername : ""));
        }
    }
}
