using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

namespace LegendOfKeepers.BattleEventInspector.Execution;

// This game invokes the speed button through a serialized native UnityEvent,
// which does not pass through Harmony on this build.  The AUTO control is
// therefore two native Button clones at the same position: OFF invokes the
// built-in GameObject.SetActive(true) on ON; ON invokes SetActive(false) on
// itself, exposing OFF beneath it.  No managed click delegate, MonoBehaviour,
// coroutine, frame polling, input simulation, or game-speed callback exists.
internal static class OneStepButtonController
{
    private const string Source = "OneStepButtonController";
    private const string IconResource = "LegendOfKeepers.BattleEventInspector.assets.autobattle-icon.png";
    private const float GapPixels = 8f;

    // x86 IL2CPP object layout, verified from the generated CoreModule
    // metadata for this exact game build.  All writes are restricted to the
    // freshly-instantiated clone's UnityEvent serialization data.
    private const int PersistentCallGroupCallsOffset = 0x08;
    private const int PersistentCallTargetOffset = 0x08;
    private const int PersistentCallMethodNameOffset = 0x0C;
    private const int PersistentCallModeOffset = 0x10;
    private const int PersistentCallArgumentsOffset = 0x14;
    private const int PersistentCallStateOffset = 0x18;
    private const int ArgumentCacheBoolOffset = 0x1C;
    private const int UnityEventPersistentCallsOffset = 0x0C;
    private const int UnityEventCallsDirtyOffset = 0x10;
    private const int Il2CppListItemsOffset = 0x08;
    private const int Il2CppListSizeOffset = 0x0C;
    private const int Il2CppArrayDataOffset = 0x10;
    private const int Il2CppStringLengthOffset = 0x08;
    private const int Il2CppStringCharsOffset = 0x0C;
    private const int PersistentListenerModeBool = 6;
    private const int UnityEventCallStateRuntimeOnly = 2;

    private static bool _enabled;
    // These must be strong managed wrappers.  Unity owns the native objects,
    // but a WeakReference lets the wrapper disappear while its visible native
    // control remains alive, causing a false OFF reading after GC.
    private static GameObject? _offObject;
    private static GameObject? _onObject;
    // The AUTO choice belongs to the run, rather than to a particular copy of
    // the Dungeon HUD.  The game destroys and recreates that HUD between
    // rooms/fights, so retain this choice when its native clones are rebuilt.
    private static bool _autoRequested;
    private static bool _iconLoadAttempted;
    private static Texture2D? _iconTexture;
    private static Sprite? _iconSprite;
    private static IntPtr _setActiveMethodNamePointer;

    // Clone visibility is the native visual representation, while this value
    // survives the HUD destruction that occurs between fights.
    public static bool IsAutoBattleEnabled => _enabled && _autoRequested;

    public static void Initialize(InspectorSettings settings)
    {
        _enabled = settings.OneStepButtonEnabled;
        if (!_enabled) _autoRequested = false;
    }

    public static void OnDungeonUiReady(DungeonMain _)
    {
        EnsureTopRightToggle();
    }

    public static void OnAttackBarVisible(AttackBar _)
    {
        // This is a stable point after a room/fight transition.  Existing
        // controls are only checked here; they are not cloned or rewritten on
        // every monster turn.
        EnsureTopRightToggle(stableHud: true);
    }

    public static void OnMasterSpellBarVisible()
    {
        // The master-choice overlay can be the first stable UI after a fight.
        EnsureTopRightToggle(stableHud: true);
    }

    public static void OnAttackBarHidden()
    {
        // Deliberately no reset: the requested AUTO state is a run setting,
        // not an attack-bar setting.
    }

    // Retained for existing single-step plumbing.  It must not overwrite the
    // visual state selected by the player.
    public static void RefreshLabel() { }

    // This is reached only from the Harmony observation of GameObject.SetActive.
    // The persistent UnityEvent stored in the ON button invokes SetActive(true)
    // itself; no managed button listener, coroutine, or input emulation is
    // introduced here.
    public static void OnGameObjectSetActive(GameObject changedObject, bool active)
    {
        if (!_enabled) return;
        try
        {
            if (!TryGetObject(_onObject, out var onObject) || onObject is null || changedObject.Pointer != onObject.Pointer) return;
            if (_autoRequested == active) return;

            _autoRequested = active;
            if (active)
            {
                Emit("AutoBattleToggleEnabled", "native ON clone activation observed; attempting the current visible MonsterTurn, MasterChoice, or DisasterChoice once");
                AutoBattleController.OnAutoToggleEnabled();
                MasterAutoBattleController.OnAutoToggleEnabled();
                DisasterAutoBattleController.OnAutoToggleEnabled();
            }
            else
            {
                Emit("AutoBattleToggleDisabled", "native ON clone deactivated");
            }
        }
        catch (Exception exception)
        {
            Emit("AutoBattleToggleException", exception.ToString());
        }
    }

    private static void EnsureTopRightToggle(bool stableHud = false)
    {
        if (!_enabled) return;
        try
        {
            if (IntPtr.Size != 4)
            {
                Emit("AutoBattleToggleUnavailable", $"expected x86 process but IntPtr.Size={IntPtr.Size}");
                return;
            }

            var dungeon = DungeonMain.instance;
            var template = dungeon?.dungeonSpeedBT;
            if (template is null || template.gameObject is null || template.transform.parent is null)
            {
                Emit("AutoBattleToggleUnavailable", "DungeonMain.dungeonSpeedBT is unavailable");
                return;
            }

            var sourceRect = template.GetComponent<RectTransform>();
            if (sourceRect is null)
            {
                Emit("AutoBattleToggleUnavailable", "DungeonMain.dungeonSpeedBT has no RectTransform");
                return;
            }

            if (TryGetObject(_offObject, out var existingOff) && existingOff is not null &&
                TryGetObject(_onObject, out var existingOn) && existingOn is not null)
            {
                if (existingOff.transform.parent is not null && existingOn.transform.parent is not null &&
                    existingOff.transform.parent.Pointer == template.transform.parent.Pointer &&
                    existingOn.transform.parent.Pointer == template.transform.parent.Pointer &&
                    TryRefreshToggleVisual(existingOff, bright: false) &&
                    TryRefreshToggleVisual(existingOn, bright: true))
                {
                    return;
                }

                if (!stableHud) return;

                // A clone from another HUD, or one with missing visual/button
                // components, cannot be repaired safely.  Rebuild it once at
                // this stable UI boundary; never once per turn.
                UnityEngine.Object.Destroy(existingOff);
                UnityEngine.Object.Destroy(existingOn);
                _offObject = null;
                _onObject = null;
                Emit("AutoBattleToggleRebuildRequired", "existing AUTO clone could not be rehydrated; rebuilding from the current native speed button");
            }

            GameObject? offObject = null;
            GameObject? onObject = null;
            try
            {
                offObject = CreateToggleClone(template, sourceRect, "BattleEventInspector.AutoBattleOff", bright: false, initiallyActive: false, out var offIcon);
                onObject = CreateToggleClone(template, sourceRect, "BattleEventInspector.AutoBattleOn", bright: true, initiallyActive: false, out var onIcon);
                if (offObject is null || onObject is null || offIcon is null || onIcon is null)
                    throw new InvalidOperationException("AUTO toggle clone was not constructed");

                // Both callbacks must be rewritten before either clone can
                // receive an input event.  A failed validation destroys the
                // clones, so no accidental speed callback can remain.
                var offConfigured = TryReplaceSpeedCallbackWithSetActive(offObject, onObject, active: true, out var offReason);
                var onConfigured = TryReplaceSpeedCallbackWithSetActive(onObject, onObject, active: false, out var onReason);
                if (!offConfigured || !onConfigured)
                {
                    Emit("AutoBattleToggleUnavailable", $"native callback replacement rejected; off={offReason}; on={onReason}");
                    UnityEngine.Object.Destroy(offObject);
                    UnityEngine.Object.Destroy(onObject);
                    return;
                }

                EnableOnlyPrimaryButton(offObject);
                EnableOnlyPrimaryButton(onObject);

                var sourceIndex = template.transform.GetSiblingIndex();
                offObject.transform.SetSiblingIndex(sourceIndex);
                onObject.transform.SetSiblingIndex(sourceIndex + 1);
                // OFF must always remain alive beneath ON.  When the HUD is
                // rebuilt while AUTO is enabled, hiding OFF here would make
                // the control disappear forever as soon as ON is clicked to
                // turn AUTO off in the next fight.
                offObject.SetActive(true);
                onObject.SetActive(_autoRequested);

                _offObject = offObject;
                _onObject = onObject;
                Emit("AutoBattleToggleConfigured", $"two native AUTO controls placed left of DungeonMain.dungeonSpeedBT at x={offObject.GetComponent<RectTransform>().anchoredPosition.x:F1}; offCallback=GameObject.SetActive(true); onCallback=GameObject.SetActive(false); retainedState={_autoRequested}");
            }
            catch
            {
                if (offObject is not null) UnityEngine.Object.Destroy(offObject);
                if (onObject is not null) UnityEngine.Object.Destroy(onObject);
                throw;
            }
        }
        catch (Exception exception)
        {
            Emit("AutoBattleToggleException", exception.ToString());
        }
    }

    private static GameObject? CreateToggleClone(Button template, RectTransform sourceRect, string name, bool bright, bool initiallyActive, out Image? iconImage)
    {
        iconImage = null;
        var clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
        clone.name = name;
        clone.SetActive(false);

        var buttons = clone.GetComponentsInChildren<Button>(true);
        var managers = clone.GetComponentsInChildren<ButtonManager>(true);
        var customButtons = clone.GetComponentsInChildren<CustomButton>(true);
        var images = clone.GetComponentsInChildren<Image>(true);
        var cloneRect = clone.GetComponent<RectTransform>();
        var primaryButton = FindPrimaryButton(clone, buttons);
        iconImage = FindPrimaryImage(primaryButton, images);
        if (cloneRect is null || primaryButton is null || iconImage is null)
        {
            UnityEngine.Object.Destroy(clone);
            return null;
        }

        foreach (var button in buttons)
        {
            button.transition = Selectable.Transition.None;
            button.interactable = false;
            button.enabled = false;
        }
        foreach (var manager in managers)
            manager.enabled = false;
        foreach (var customButton in customButtons)
            customButton.inactive = true;
        HideImages(images);

        if (!ApplyAutoBattleIcon(iconImage))
        {
            UnityEngine.Object.Destroy(clone);
            iconImage = null;
            return null;
        }

        cloneRect.anchorMin = sourceRect.anchorMin;
        cloneRect.anchorMax = sourceRect.anchorMax;
        cloneRect.pivot = sourceRect.pivot;
        cloneRect.sizeDelta = sourceRect.sizeDelta;
        cloneRect.localScale = sourceRect.localScale;
        cloneRect.anchoredPosition = sourceRect.anchoredPosition - new Vector2(Mathf.Max(sourceRect.rect.width, 48f) + GapPixels, 0f);
        iconImage.raycastTarget = true;
        // Disabled remains neutral, but it must remain visibly clickable.
        iconImage.color = bright ? Color.white : new Color(0.56f, 0.56f, 0.56f, 1f);
        clone.SetActive(initiallyActive);
        return clone;
    }

    private static bool TryRefreshToggleVisual(GameObject root, bool bright)
    {
        try
        {
            var buttons = root.GetComponentsInChildren<Button>(true);
            var images = root.GetComponentsInChildren<Image>(true);
            var primaryButton = FindPrimaryButton(root, buttons);
            var iconImage = FindPrimaryImage(primaryButton, images);
            if (primaryButton is null || iconImage is null) return false;

            // Unity can unload a runtime-created Sprite between rooms and
            // replace it with its plain white fallback. Restore our icon if
            // it is missing or no longer the sprite we created; never rewrite
            // the clone's native button callback during a turn.
            var hasExpectedIcon = _iconSprite != null && iconImage.sprite != null && iconImage.sprite.Pointer == _iconSprite.Pointer;
            if (!hasExpectedIcon && !ApplyAutoBattleIcon(iconImage)) return false;
            iconImage.raycastTarget = true;
            iconImage.color = bright ? Color.white : new Color(0.56f, 0.56f, 0.56f, 1f);
            primaryButton.transition = Selectable.Transition.None;
            primaryButton.enabled = true;
            primaryButton.interactable = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Button? FindPrimaryButton(GameObject root, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Button> buttons)
    {
        var rootButton = root.GetComponent<Button>();
        if (rootButton is not null) return rootButton;
        foreach (var button in buttons)
            if (button is not null) return button;
        return null;
    }

    private static Image? FindPrimaryImage(Button? button, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Image> images)
    {
        if (button is not null && button.image is not null) return button.image;
        foreach (var image in images)
            if (image is not null) return image;
        return null;
    }

    private static void EnableOnlyPrimaryButton(GameObject root)
    {
        var buttons = root.GetComponentsInChildren<Button>(true);
        var primary = FindPrimaryButton(root, buttons);
        foreach (var button in buttons)
        {
            if (button is null) continue;
            var primaryButton = button!;
            var enabled = primaryButton == primary;
            primaryButton.enabled = enabled;
            primaryButton.interactable = enabled;
        }
    }

    // Replace the cloned template's serialized speed callback in-place.  This
    // uses the generated metadata offsets above, accepts either the original
    // speed method or our prior SetActive rewrite, and dirties the clone's
    // call cache.
    // The original button and all game assets are never touched.
    private static bool TryReplaceSpeedCallbackWithSetActive(GameObject clone, GameObject target, bool active, out string reason)
    {
        reason = "unknown";
        try
        {
            var buttons = clone.GetComponentsInChildren<Button>(true);
            var primary = FindPrimaryButton(clone, buttons);
            if (primary is null || primary.onClick is null || primary.onClick.Pointer == IntPtr.Zero)
            {
                reason = "primary Button or onClick is unavailable";
                return false;
            }

            var eventPointer = primary.onClick.Pointer;
            var groupPointer = ReadPointer(eventPointer, UnityEventPersistentCallsOffset);
            if (groupPointer == IntPtr.Zero)
            {
                reason = "UnityEvent has no PersistentCallGroup";
                return false;
            }
            var callsPointer = ReadPointer(groupPointer, PersistentCallGroupCallsOffset);
            if (callsPointer == IntPtr.Zero)
            {
                reason = "PersistentCallGroup has no call list";
                return false;
            }
            var itemPointer = ReadPointer(callsPointer, Il2CppListItemsOffset);
            var count = Marshal.ReadInt32(IntPtr.Add(callsPointer, Il2CppListSizeOffset));
            if (itemPointer == IntPtr.Zero || count < 1 || count > 8)
            {
                reason = $"invalid persistent-call collection (count={count})";
                return false;
            }

            if (!TryGetSetActiveMethodName(out var setActiveMethodNamePointer) || setActiveMethodNamePointer == IntPtr.Zero || target.Pointer == IntPtr.Zero)
            {
                reason = "native SetActive method name or target is unavailable";
                return false;
            }

            for (var index = 0; index < count; index++)
            {
                var callPointer = ReadPointer(itemPointer, Il2CppArrayDataOffset + (index * IntPtr.Size));
                if (callPointer == IntPtr.Zero)
                {
                    reason = $"persistent call {index} is null";
                    return false;
                }

                var originalMethod = ReadIl2CppString(ReadPointer(callPointer, PersistentCallMethodNameOffset));
                if (!string.Equals(originalMethod, "SwitchDungeonSpeed", StringComparison.Ordinal) &&
                    !string.Equals(originalMethod, "SetActive", StringComparison.Ordinal))
                {
                    reason = $"persistent call {index} is '{originalMethod ?? "<null>"}', expected SwitchDungeonSpeed or prior AUTO SetActive";
                    return false;
                }

                var argumentsPointer = ReadPointer(callPointer, PersistentCallArgumentsOffset);
                if (argumentsPointer == IntPtr.Zero)
                {
                    reason = $"persistent call {index} has no ArgumentCache";
                    return false;
                }

                Marshal.WriteIntPtr(IntPtr.Add(callPointer, PersistentCallTargetOffset), target.Pointer);
                Marshal.WriteIntPtr(IntPtr.Add(callPointer, PersistentCallMethodNameOffset), setActiveMethodNamePointer);
                Marshal.WriteInt32(IntPtr.Add(callPointer, PersistentCallModeOffset), PersistentListenerModeBool);
                Marshal.WriteByte(IntPtr.Add(argumentsPointer, ArgumentCacheBoolOffset), active ? (byte)1 : (byte)0);
                Marshal.WriteInt32(IntPtr.Add(callPointer, PersistentCallStateOffset), UnityEventCallStateRuntimeOnly);
            }

            // Remove any cloned runtime listeners, then force Unity to rebuild
            // its persistent call list from the rewritten native fields.
            primary.onClick.RemoveAllListeners();
            Marshal.WriteByte(IntPtr.Add(eventPointer, UnityEventCallsDirtyOffset), 1);
            reason = $"rewrote {count} persistent call(s)";
            return true;
        }
        catch (Exception exception)
        {
            reason = exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    private static IntPtr ReadPointer(IntPtr basePointer, int offset)
    {
        if (basePointer == IntPtr.Zero) return IntPtr.Zero;
        return Marshal.ReadIntPtr(IntPtr.Add(basePointer, offset));
    }

    private static string? ReadIl2CppString(IntPtr stringPointer)
    {
        if (stringPointer == IntPtr.Zero) return null;
        var length = Marshal.ReadInt32(IntPtr.Add(stringPointer, Il2CppStringLengthOffset));
        if (length < 0 || length > 128) return null;
        return Marshal.PtrToStringUni(IntPtr.Add(stringPointer, Il2CppStringCharsOffset), length);
    }

    private static bool TryGetSetActiveMethodName(out IntPtr methodNamePointer)
    {
        methodNamePointer = _setActiveMethodNamePointer;
        if (methodNamePointer != IntPtr.Zero) return true;
        try
        {
            methodNamePointer = Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp("SetActive");
            if (methodNamePointer == IntPtr.Zero) return false;
            _setActiveMethodNamePointer = methodNamePointer;
            return true;
        }
        catch (Exception exception)
        {
            Emit("AutoBattleToggleException", $"could not create native method name: {exception}");
            return false;
        }
    }

    private static void HideImages(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Image> images)
    {
        foreach (var image in images)
        {
            if (image is null) continue;
            image.color = new Color(1f, 1f, 1f, 0f);
            image.raycastTarget = false;
        }
    }

    private static bool ApplyAutoBattleIcon(Image image)
    {
        if (image is null || !TryLoadAutoBattleIcon(out var sprite) || sprite is null) return false;
        image.sprite = sprite;
        image.preserveAspect = true;
        image.type = Image.Type.Simple;
        return true;
    }

    private static bool TryLoadAutoBattleIcon(out Sprite? sprite)
    {
        sprite = _iconSprite;
        // `is not null` only checks the managed wrapper.  Unity may destroy
        // the underlying runtime Sprite/Texture on a room transition while
        // leaving that wrapper alive, which previously produced the white
        // square seen on the next fight.
        if (_iconSprite != null && _iconTexture != null) return true;
        _iconSprite = null;
        _iconTexture = null;
        if (_iconLoadAttempted)
        {
            // The former resource may have been unloaded, so a fresh load is
            // both safe and required for the new HUD.
            _iconLoadAttempted = false;
        }
        _iconLoadAttempted = true;
        try
        {
            using var stream = typeof(OneStepButtonController).Assembly.GetManifestResourceStream(IconResource);
            if (stream is null)
            {
                Emit("AutoBattleToggleUnavailable", $"embedded icon resource is missing: {IconResource}");
                return false;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, buffer.ToArray(), false))
            {
                UnityEngine.Object.Destroy(texture);
                Emit("AutoBattleToggleUnavailable", "ImageConversion.LoadImage returned false for the embedded AUTO icon");
                return false;
            }

            texture.filterMode = FilterMode.Point;
            var inset = Mathf.Floor(Mathf.Min(texture.width, texture.height) * 0.13f);
            var iconRect = new Rect(inset, inset, texture.width - (2f * inset), texture.height - (2f * inset));
            var generatedSprite = Sprite.Create(texture, iconRect, new Vector2(0.5f, 0.5f), 100f);
            if (generatedSprite is null)
            {
                UnityEngine.Object.Destroy(texture);
                Emit("AutoBattleToggleUnavailable", "Sprite.Create returned null for the embedded AUTO icon");
                return false;
            }

            _iconTexture = texture;
            _iconSprite = generatedSprite;
            sprite = generatedSprite;
            return true;
        }
        catch (Exception exception)
        {
            Emit("AutoBattleToggleException", exception.ToString());
            return false;
        }
    }

    private static bool TryGetObject(GameObject? reference, out GameObject? gameObject)
    {
        gameObject = null;
        if (reference is null || reference == null) return false;
        gameObject = reference;
        return true;
    }

    private static void Emit(string eventName, string reason) => ActionStateInspector.EmitResearchEvent(Source, "auto-battle-toggle", eventName, new
    {
        battleId = ActionStateInspector.CurrentBattleId,
        turnId = ActionStateInspector.CurrentTurnId,
        enabled = IsAutoBattleEnabled,
        reason,
    });
}
