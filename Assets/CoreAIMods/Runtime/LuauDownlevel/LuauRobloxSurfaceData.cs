namespace CoreAI.Infrastructure.Luau
{
    /// <summary>
    /// What a Roblox script may name, read from the offline Roblox mirror: the Lua and Roblox globals
    /// (<c>reference/engine/globals</c>), the members of every Luau library (<c>reference/engine/libraries</c>),
    /// the datatype names (<c>reference/engine/datatypes</c>) and the service classes of the API dump. Mirror
    /// 0.730.0.7300790, API dump 0.731.0.7310942. <c>LuauExportLintSurfaceEditModeTests</c>
    /// compares it with the mirror whenever the mirror is present.
    /// </summary>
    internal static class LuauRobloxSurfaceData
    {
        /// <summary>The mirror version the lists were read from.</summary>
        public const string MirrorVersion = "0.730.0.7300790";

        /// <summary>Global names a Roblox script has: Lua and Roblox globals and the library tables.</summary>
        public static readonly string[] Globals =
        {
            "DebuggerManager", "Enum", "PluginManager", "UserSettings", "_G", "_VERSION", "assert", "bit32",
            "buffer", "collectgarbage", "coroutine", "debug", "delay", "elapsedTime", "error", "game", "gcinfo",
            "getfenv", "getmetatable", "ipairs", "loadstring", "math", "newproxy", "next", "os", "pairs", "pcall",
            "plugin", "print", "printidentity", "rawequal", "rawget", "rawlen", "rawset", "require", "script",
            "select", "setfenv", "setmetatable", "settings", "shared", "spawn", "stats", "string", "table", "task",
            "tick", "time", "tonumber", "tostring", "type", "typeof", "unpack", "utf8", "vector", "version", "wait",
            "warn", "workspace", "xpcall", "ypcall"
        };

        /// <summary>Datatype names; the constructible ones are globals in Roblox.</summary>
        public static readonly string[] Datatypes =
        {
            "AdReward", "Axes", "BrickColor", "CFrame", "CatalogSearchParams", "Color3", "ColorSequence",
            "ColorSequenceKeypoint", "Content", "DateTime", "DockWidgetPluginGuiInfo", "Enum", "EnumItem", "Enums",
            "Faces", "FloatCurveKey", "Font", "Instance", "NumberRange", "NumberSequence", "NumberSequenceKeypoint",
            "OverlapParams", "Path2DControlPoint", "PathWaypoint", "PhysicalProperties", "RBXScriptConnection",
            "RBXScriptSignal", "Random", "Ray", "RaycastParams", "RaycastResult", "Rect", "Region3", "Region3int16",
            "RotationCurveKey", "Secret", "SecurityCapabilities", "SharedTable", "TweenInfo", "UDim", "UDim2",
            "User", "ValueCurveKey", "Vector2", "Vector2int16", "Vector3", "Vector3int16"
        };

        /// <summary>Every class the API dump tags as a service.</summary>
        public static readonly string[] Services =
        {
            "AccountService", "AchievementService", "ActivityHistoryEventService", "AdService", "AnalyticsService",
            "AnimationClipProvider", "AnimationFromVideoCreatorService", "AnimationFromVideoCreatorStudioService",
            "AnnotationsService", "AppAgeSignalsService", "AppLifecycleObserverService", "AppRatingPromptService",
            "AppStorageService", "AppUpdateService", "AssetCounterService", "AssetDeliveryProxy",
            "AssetImportService", "AssetManagerService", "AssetQualityService", "AssetService", "AudioFocusService",
            "AuroraScriptService", "AuroraService", "AvatarChatService", "AvatarCreationService",
            "AvatarEditorService", "AvatarImportService", "AvatarSettings", "BadgeService", "BrowserService",
            "BugReporterService", "BulkImportService", "CSGDictionaryService", "CacheableContentProvider",
            "CalloutService", "CaptureService", "ChangeHistoryService", "ChangeHistoryStreamingService", "Chat",
            "ClientStorageService", "CloudCRUDService", "CloudExecutionService", "ClusterPacketCache",
            "CollaboratorsService", "CollectionService", "CommerceService", "ConfigService",
            "ConfigureServerService", "ConnectivityService", "ContentProvider", "ContextActionService",
            "ControllerService", "CookiesService", "CoreGui", "CoreGuiConfiguration", "CorePackages",
            "CoreScriptDebuggingManagerHelper", "CoreScriptSyncService", "CreationDBService", "CreatorStoreService",
            "CrossDMScriptChangeListener", "DataModelPatchService", "DataStoreService", "Debris", "DebugSettings",
            "DebuggablePluginWatcher", "DebuggerConnectionManager", "DebuggerManager", "DebuggerUIService",
            "DeferredAssetManagerService", "DesignFoundationsService", "DeviceDisplayService", "DeviceIdService",
            "DraftsService", "DraggerService", "EditableService", "EditorSourceService", "EncodingService",
            "EventIngestService", "ExampleV2Service", "ExperienceAuthService", "ExperienceNotificationService",
            "ExperienceService", "ExperienceStateCaptureService", "ExperienceStateRecordingService",
            "ExplorerServiceVisibilityService", "FaceAnimatorService", "FacialAgeEstimationService",
            "FacialAnimationRecordingService", "FacialAnimationStreamingServiceV2", "FeatureRestrictionManager",
            "FileManagerService", "FileSyncReplicationService", "FlagStandService", "FlyweightService",
            "FriendService", "GamePassService", "GameSettings", "GamepadService", "GenerationService",
            "GenericChallengeService", "Geometry", "GeometryService", "GongService", "GroupService", "GuiService",
            "GuidRegistryService", "HSRDataContentProvider", "HapticService", "HarmonyService",
            "HeapProfilerService", "HeatmapQueryService", "HeatmapService", "HeightmapImporterService", "Hopper",
            "HttpRbxApiService", "HttpService", "ILegacyStudioBridge", "IXPService", "ImageScreenCaptureService",
            "IncrementalPatchBuilder", "InsertService", "InstanceExtensionsService", "InstanceFileSyncService",
            "InternalMessagingService", "InternalMessagingServiceVerifier", "InternalSyncService", "JointsService",
            "KeyboardService", "KeyframeSequenceProvider", "LanguageService", "LegacyStudioBridge", "Lighting",
            "LinkingService", "LiveScriptingService", "LiveSyncService", "LocalStorageService",
            "LocalizationService", "LodDataService", "LogReporterService", "LogService", "LoginService",
            "LuaSettings", "LuaWebService", "LuauExpressionService", "LuauScriptAnalyzerService",
            "MLModelDeliveryService", "MLService", "MarketplaceService", "MatchmakingService",
            "MaterialGenerationService", "MaterialService", "MemStorageService", "MemoryStoreService",
            "MeshContentProvider", "MessageBusService", "MessagingService", "MetaBreakpointManager",
            "MicroProfilerService", "ModerationService", "MouseService", "NetworkClient", "NetworkServer",
            "NetworkSettings", "NonReplicatedCSGDictionaryService", "NotificationService",
            "OmniRecommendationsService", "OpenCloudService", "PackageService", "PackageUIService", "Packages",
            "PartyEmulatorService", "PatchBundlerFileWatch", "PathfindingService", "PerformanceControlService",
            "PermissionsService", "PhysicsService", "PhysicsSettings", "PinShortcutService", "PlaceAssetIdsService",
            "PlaceStatsService", "PlacesService", "PlatformCloudStorageService", "PlatformFriendsService",
            "PlatformLibraries", "PlayerDataService", "PlayerEmulatorService", "PlayerHydrationService",
            "PlayerViewService", "Players", "PluginConnectionService", "PluginDebugService", "PluginGuiService",
            "PluginManagementService", "PluginPolicyService", "PointsService", "PolicyService", "PopLatencyService",
            "Preloaded", "ProceduralBehaviorSchedulerService", "ProcessInstancePhysicsService",
            "ProximityPromptService", "PublishService", "RbxAnalyticsService", "RecommendationService",
            "ReflectionService", "RemoteCommandService", "RemoteCursorService", "RemoteDebuggerServer",
            "RenderSettings", "ReplicatedFirst", "ReplicatedStorage", "RequestOrchestratorService",
            "RibbonNotificationService", "RobloxPluginGuiService", "RobloxReplicatedStorage", "RobloxServerStorage",
            "RolloutValidationService", "RomarkRbxAnalyticsService", "RomarkService", "RtMessagingService",
            "RunService", "RuntimeContentService", "RuntimeScriptService", "SafetyService", "SceneAnalysisService",
            "ScriptChangeService", "ScriptCloneWatcher", "ScriptCloneWatcherHelper", "ScriptCommitService",
            "ScriptContext", "ScriptDebuggerService", "ScriptEditorService", "ScriptProfilerService",
            "ScriptRegistrationService", "ScriptService", "Selection", "SelectionHighlightManager",
            "SerializationService", "ServerScriptService", "ServerStorage", "ServiceVisibilityService",
            "SessionCheckService", "SessionService", "SharedTableRegistry", "SlimAnimationReplicationService",
            "SlimContentProvider", "SlimDebugSettings", "SlimReplicationService", "SlimService",
            "SmoothVoxelsUpgraderService", "SnippetService", "SocialService", "SolidModelContentProvider",
            "SoundService", "SoundShimService", "SpawnerService", "StartPageService", "StarterGui", "StarterPack",
            "StarterPlayer", "StartupMessageService", "Stats", "StopWatchReporter", "Studio", "StudioAssetService",
            "StudioCameraService", "StudioCaptureService", "StudioData", "StudioDeviceEmulatorService",
            "StudioDeviceSimulatorService", "StudioPublishService", "StudioScriptDebugEventListener",
            "StudioSdkService", "StudioService", "StudioTestService", "StudioUserService", "StudioWidgetsService",
            "StylingService", "SystemThemeService", "TaskScheduler", "TeamCreateData", "TeamCreatePublishService",
            "TeamCreateService", "Teams", "TelemetryService", "TeleportService", "TemporaryCageMeshProvider",
            "TemporaryScriptService", "TestService", "TextBoxService", "TextChatService", "TextService",
            "TextureGenerationService", "ThirdPartyUserService", "TimerService", "ToastNotificationService",
            "TouchInputService", "TraceRouteService", "TracerService", "TutorialService", "TweenService",
            "UGCAvatarService", "UGCValidationService", "UIDragDetectorService", "UniqueIdLookupService",
            "UnvalidatedAssetService", "UserGameSettings", "UserInputService", "UserService", "UserStorageService",
            "VRService", "VRStatusService", "VersionControlService", "VideoCaptureService",
            "VideoScreenCaptureService", "VideoService", "VirtualInputManager", "VirtualUser",
            "VisibilityCheckDispatcher", "Visit", "VisualizationModeService", "VoiceChatInternal",
            "VoiceChatService", "WebSocketService", "WebViewService", "WindowProtocolService", "Workspace",
            "WrapDeformMeshProvider"
        };

        /// <summary>Each Luau library and its members, functions and values alike.</summary>
        public static readonly string[][] LibraryMembers =
        {
            new[]
            {
                "bit32", "arshift", "band", "bnot", "bor", "btest", "bxor", "byteswap", "countlz", "countrz",
                "extract", "lrotate", "lshift", "replace", "rrotate", "rshift"
            },
            new[]
            {
                "buffer", "copy", "create", "fill", "fromstring", "len", "readbits", "readf32", "readf64", "readi16",
                "readi32", "readi8", "readstring", "readu16", "readu32", "readu8", "tostring", "writebits",
                "writef32", "writef64", "writei16", "writei32", "writei8", "writestring", "writeu16", "writeu32",
                "writeu8"
            },
            new[]
            {
                "coroutine", "close", "create", "isyieldable", "resume", "running", "status", "wrap", "yield"
            },
            new[]
            {
                "debug", "dumpcodesize", "getmemorycategory", "info", "profilebegin", "profileend",
                "resetmemorycategory", "setmemorycategory", "traceback"
            },
            new[]
            {
                "math", "abs", "acos", "asin", "atan", "atan2", "ceil", "clamp", "cos", "cosh", "deg", "e", "exp",
                "floor", "fmod", "frexp", "huge", "isfinite", "isinf", "isnan", "ldexp", "lerp", "log", "log10",
                "map", "max", "min", "modf", "nan", "noise", "phi", "pi", "pow", "rad", "random", "randomseed",
                "round", "sign", "sin", "sinh", "sqrt", "sqrt2", "tan", "tanh", "tau"
            },
            new[]
            {
                "os", "clock", "date", "difftime", "time"
            },
            new[]
            {
                "string", "byte", "char", "find", "format", "gmatch", "gsub", "len", "lower", "match", "pack",
                "packsize", "rep", "reverse", "split", "sub", "unpack", "upper"
            },
            new[]
            {
                "table", "clear", "clone", "concat", "create", "find", "foreach", "foreachi", "freeze", "getn",
                "insert", "isfrozen", "maxn", "move", "pack", "remove", "sort", "unpack"
            },
            new[]
            {
                "task", "cancel", "defer", "delay", "desynchronize", "spawn", "synchronize", "wait"
            },
            new[]
            {
                "utf8", "char", "charpattern", "codepoint", "codes", "graphemes", "len", "nfcnormalize",
                "nfdnormalize", "offset"
            },
            new[]
            {
                "vector", "abs", "angle", "ceil", "clamp", "create", "cross", "dot", "floor", "lerp", "magnitude",
                "max", "min", "normalize", "one", "sign", "zero"
            }
        };
    }
}
