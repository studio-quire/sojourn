// Copied from Radegast Legacy's contrib tools (projects/radegast/tools/QuireUia, written by Claude Opus 5.5).
using System;
using System.Runtime.InteropServices;

// UI Automation events through the COM client (UIAutomationCore), for where the managed client's event registration
// fails (it does on a hidden desktop). Only the methods used are typed; the rest hold their vtable slots.
static class ComUia
{
    const int UIA_LiveRegionChangedEventId = 20024;
    const int NamePropertyId = 30005, ProcessIdPropertyId = 30002, ControlTypePropertyId = 30003,
        AutomationIdPropertyId = 30011, NativeWindowHandlePropertyId = 30020, LocalizedControlTypePropertyId = 30004;

    static IUIAutomation5 uia;
    static readonly Handlers handlers = new Handlers();
    public static Func<int> Pid = () => 0;

    // Which registrations took. On a hidden desktop with no window on it yet, the notification and live-region ones fail
    // with 0x8007029C, the managed client's too (seen 2026-10-29); once a window is there they work, so the server
    // tries them again until they do.
    public static volatile bool Focus, Notification, LiveRegion;
    static IUIAutomationElement root;

    public static void Start()
    {
        uia = (IUIAutomation5)new CUIAutomation8();
        root = uia.GetRootElement();
        Focus = Try("com focus", () => uia.AddFocusChangedEventHandler(null, handlers), true);
        Retry(true);
    }

    public static void Retry(bool logFailure)
    {
        if (!Notification)
            Notification = Try("com notification", () => uia.AddNotificationEventHandler(root, 4 /* subtree */, null, handlers), logFailure);
        if (!LiveRegion)
            LiveRegion = Try("com liveregion", () => uia.AddAutomationEventHandler(UIA_LiveRegionChangedEventId, root, 4, null, handlers), logFailure);
    }

    static bool Try(string what, Action a, bool logFailure)
    {
        try { a(); Server.Log("server", "listening", what); return true; }
        catch (Exception e)
        {
            if (logFailure) Server.Log("server", "unavailable", what + ": " + e.Message + (what == "com focus" ? "" : " (trying again each second)"));
            return false;
        }
    }

    static object Prop(IUIAutomationElement e, int id)
    {
        try { return e.GetCurrentPropertyValue(id); } catch { return null; }
    }

    static bool Watched(IUIAutomationElement e)
    {
        int pid = Pid();
        return pid == 0 || (Prop(e, ProcessIdPropertyId) is int p && p == pid);
    }

    static string Describe(IUIAutomationElement e)
    {
        var hwnd = Prop(e, NativeWindowHandlePropertyId);
        return Prop(e, LocalizedControlTypePropertyId) + " " + Server.J(Prop(e, NamePropertyId) as string) +
               (Prop(e, AutomationIdPropertyId) is string id && id.Length > 0 ? " id=" + id : "") +
               (hwnd is int h && h != 0 ? " hwnd=" + h.ToString("x") : "");
    }

    [ComVisible(true)]
    class Handlers : IUIAutomationFocusChangedEventHandler, IUIAutomationEventHandler, IUIAutomationNotificationEventHandler
    {
        public void HandleFocusChangedEvent(IUIAutomationElement sender)
        {
            if (Watched(sender)) Server.Log("uia", "focus", Describe(sender));
        }

        public void HandleAutomationEvent(IUIAutomationElement sender, int eventId)
        {
            if (Watched(sender)) Server.Log("uia", eventId == UIA_LiveRegionChangedEventId ? "liveregion" : "event " + eventId, Describe(sender));
        }

        public void HandleNotificationEvent(IUIAutomationElement sender, int kind, int processing, string displayString, string activityId)
        {
            if (!Watched(sender)) return;
            string[] kinds = { "ItemAdded", "ItemRemoved", "ActionCompleted", "ActionAborted", "Other" };
            string[] procs = { "ImportantAll", "ImportantMostRecent", "All", "MostRecent", "CurrentThenMostRecent" };
            Server.Log("uia", "notification", displayString,
                "\"notificationKind\":" + Server.J(kind >= 0 && kind < kinds.Length ? kinds[kind] : kind.ToString()) +
                ",\"processing\":" + Server.J(processing >= 0 && processing < procs.Length ? procs[processing] : processing.ToString()) +
                ",\"activity\":" + Server.J(activityId) + ",\"element\":" + Server.J(Describe(sender)));
        }
    }

    [ComImport, Guid("e22ad333-b25f-460c-83d0-0581107395c9")]
    class CUIAutomation8 { }

    [ComImport, Guid("25F700C8-D816-4057-A9DC-3CBDEE77E256"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation5
    {
        // IUIAutomation
        void CompareElements(); void CompareRuntimeIds();
        IUIAutomationElement GetRootElement();
        void ElementFromHandle(); void ElementFromPoint(); void GetFocusedElement();
        void GetRootElementBuildCache(); void ElementFromHandleBuildCache(); void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache(); void CreateTreeWalker(); void get_ControlViewWalker(); void get_ContentViewWalker();
        void get_RawViewWalker(); void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition();
        void CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition(); void CreatePropertyCondition();
        void CreatePropertyConditionEx(); void CreateAndCondition(); void CreateAndConditionFromArray();
        void CreateAndConditionFromNativeArray(); void CreateOrCondition(); void CreateOrConditionFromArray();
        void CreateOrConditionFromNativeArray(); void CreateNotCondition();
        void AddAutomationEventHandler(int eventId, IUIAutomationElement element, int scope,
            [MarshalAs(UnmanagedType.IUnknown)] object cacheRequest, IUIAutomationEventHandler handler);
        void RemoveAutomationEventHandler(); void AddPropertyChangedEventHandlerNativeArray(); void AddPropertyChangedEventHandler();
        void RemovePropertyChangedEventHandler(); void AddStructureChangedEventHandler(); void RemoveStructureChangedEventHandler();
        void AddFocusChangedEventHandler([MarshalAs(UnmanagedType.IUnknown)] object cacheRequest, IUIAutomationFocusChangedEventHandler handler);
        void RemoveFocusChangedEventHandler(); void RemoveAllEventHandlers(); void IntNativeArrayToSafeArray();
        void IntSafeArrayToNativeArray(); void RectToVariant(); void VariantToRect(); void SafeArrayToRectNativeArray();
        void CreateProxyFactoryEntry(); void get_ProxyFactoryMapping(); void GetPropertyProgrammaticName();
        void GetPatternProgrammaticName(); void PollForPotentialSupportedPatterns(); void PollForPotentialSupportedProperties();
        void CheckNotSupported(); void get_ReservedNotSupportedValue(); void get_ReservedMixedAttributeValue();
        void ElementFromIAccessible(); void ElementFromIAccessibleBuildCache();
        // IUIAutomation2
        void get_AutoSetFocus(); void put_AutoSetFocus(); void get_ConnectionTimeout(); void put_ConnectionTimeout();
        void get_TransactionTimeout(); void put_TransactionTimeout();
        // IUIAutomation3
        void AddTextEditTextChangedEventHandler(); void RemoveTextEditTextChangedEventHandler();
        // IUIAutomation4
        void AddChangesEventHandler(); void RemoveChangesEventHandler();
        // IUIAutomation5
        void AddNotificationEventHandler(IUIAutomationElement element, int scope,
            [MarshalAs(UnmanagedType.IUnknown)] object cacheRequest, IUIAutomationNotificationEventHandler handler);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst(); void FindAll(); void FindFirstBuildCache();
        void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
    }

    [ComImport, Guid("c270f6b5-5c69-4290-9745-7a7f97169468"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationFocusChangedEventHandler
    {
        void HandleFocusChangedEvent(IUIAutomationElement sender);
    }

    [ComImport, Guid("146c3c17-f12e-4e22-8c27-f894b9b79c69"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationEventHandler
    {
        void HandleAutomationEvent(IUIAutomationElement sender, int eventId);
    }

    [ComImport, Guid("c7cb2637-e6c2-4d0c-85de-4948c02175c7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationNotificationEventHandler
    {
        void HandleNotificationEvent(IUIAutomationElement sender, int kind, int processing,
            [MarshalAs(UnmanagedType.BStr)] string displayString, [MarshalAs(UnmanagedType.BStr)] string activityId);
    }
}
