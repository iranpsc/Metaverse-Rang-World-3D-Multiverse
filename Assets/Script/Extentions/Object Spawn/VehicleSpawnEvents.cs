using System;

namespace Meta
{
    // A simple static class to manage global spawn events.
    public static class VehicleSpawnEvents
    {
        /// <summary>
        /// Static action invoked by UI buttons. The local player's spawn controller 
        /// subscribes to this event upon gaining authority.
        /// </summary>
        public static Action<VehicleSpawnData> OnStartPreviewRequested;
    }
}