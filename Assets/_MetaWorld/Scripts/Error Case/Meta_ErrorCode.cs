using System.Collections.Generic;
using UnityEngine;

namespace Meta
{
    public static class Meta_ErrorCode
    {
        public static readonly Dictionary<int, string> Errors = new Dictionary<int, string>
        {
            // Client Error 1 to 99
            { 1 , "[Meta-001] Invalid Input Field." },
            { 2 , "[Meta-002] Unsupported Device." },
            { 3 , "[Meta-003] Action Not Allowed." },

            // Server Error 100 to 199
            { 100 , "[Meta-100] Internal Server Exception." },
            { 101 , "[Meta-101] Database Connection Failed." },
            { 102 , "[Meta-102] Resource Not Found." },

            // Game Error 200 to 299
            { 200 , "[Meta-200] Player Not Found." },
            { 201 , "[Meta-201] Invalid Game State Translation." },
            { 202 , "[Meta-202] Spawn Point Unavaible." },

            // Network Error 300 to 399
            { 300 , "[Meta-300] Connection Lost." },
            { 301 , "[Meta-301] Timeout While Waiting For Server Response." },
            { 303 , "[Meta-302] Packet Corrupted." },

            // Account Error 400 to 499
            { 400 , "[Meta-400] Invalid Username Or Password." },
            { 401 , "[Meta-401] Account Already Exist." },
            { 402 , "[Meta-402] Account Suspended." },

            // IP Ban Error 500 to 599
            { 500 , "[Meta-500] IP Address Banned." },
            { 501 , "[Meta-501] Too Many Failed Login Attempts." },

            // Authentication Error 600 to 699
            { 600 , "[Meta-600] Unauthorized Request." },
            { 601 , "[Meta-601] Token Expired." },
            { 602 , "[Meta-602] Invalid Session." },

            // Payment / Currency Error 700 to 799
            { 700 , "[Meta-700] Insufficient Funds." },
            { 701 , "[Meta-701] Invalid Transaction." },
            { 702 , "[Meta-702] Payment Provider Unavaible." },

            // Misc / Unknown
            { 999 , "[Meta-999] Somthing Went Wrong." },

        };

        public static string GetError(int _Code)
        {
            return Errors.ContainsKey(_Code) ? Errors[_Code] : $"Unknown Error Code {_Code}";
        }
    }
}

