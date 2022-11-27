﻿namespace ShortDev.Microsoft.ConnectedDevices.Protocol.Session.AppControl;

public enum AppControlType : byte
{
    LaunchUri = 0,
    LaunchUriResult,
    LaunchUriForTarget,
    NotifyAppTargetAvailableRequest = 3,
    NotifyAppTargetAvailable = 4,
    NotifyAppTargetAvailableResponse = 5,
    CallAppService = 6,
    CallAppServiceResponse,
    GetResource,
    GetResourceResponse,
    SetResource,
    SetResourceResponse
}