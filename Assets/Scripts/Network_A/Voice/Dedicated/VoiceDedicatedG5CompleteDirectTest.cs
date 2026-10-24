#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedG5CompleteDirectTest
    {
        [MenuItem("Tools/Network A/Voice/Run Complete G5 Group Topology Tests")]
        public static void RunFromEditorMenu()
        {
            try
            {
                VoiceDedicatedGroupMembershipResolverDirectTest.RunFromEditorMenu();
                VoiceDedicatedStableGroupMergePlannerDirectTest.RunFromEditorMenu();
                VoiceDedicatedGroupLeaveReformationPlannerDirectTest.RunFromEditorMenu();
                VoiceDedicatedGroupTopologyRuntimeDirectTest.RunFromEditorMenu();
                VoiceDedicatedGroupTopologyMovementRegressionDirectTest.RunFromEditorMenu();
                RunClientEditorGroupPeerMappingTest();

                Debug.Log("VOICE_G5_COMPLETE_GROUP_TOPOLOGY=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G5_COMPLETE_GROUP_TOPOLOGY=FAIL | " +
                    exception);

                throw;
            }
        }

        //* این تابع تست داخل Assembly-CSharp-Editor را بدون وابستگی معکوس Assembly runtime اجرا می‌کند.
        private static void RunClientEditorGroupPeerMappingTest()
        {
            Type testType = Type.GetType(
                "Network_A.Voice.Client.Editor.VoiceClientGroupPeerMappingDirectTest, Assembly-CSharp-Editor",
                false);

            if (testType == null)
            {
                throw new InvalidOperationException(
                    "Unity G5 client Editor test type was not loaded.");
            }

            MethodInfo runMethod = testType.GetMethod(
                "RunFromEditorMenu",
                BindingFlags.Public |
                BindingFlags.Static);

            if (runMethod == null)
            {
                throw new InvalidOperationException(
                    "Unity G5 client Editor test entry point was not found.");
            }

            runMethod.Invoke(null, null);
        }
    }
}
#endif
