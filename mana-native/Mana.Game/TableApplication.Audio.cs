using Mana.Audio;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private Rectangle? audioVolumeCapture;
    private bool audioVolumeFocused, audioPreferencesDirty;

    private void ApplyAudioMix() => audioMixer?.SetMix(preferences.Audio);

    private void SetMasterVolume(float value)
    {
        if (!float.IsFinite(value)) return;
        float volume = MathF.Round(Math.Clamp(value, 0, 1) * 100) / 100;
        if (volume == preferences.Audio.Master.Volume) return;
        preferences = preferences with { Audio = preferences.Audio with { Master = preferences.Audio.Master with { Volume = volume } } };
        audioPreferencesDirty = true;
        ApplyAudioMix();
    }

    // Audio is a local preference. It must never cancel or issue an engine action.
    private void SaveAudioPreferences()
    {
        try { preferences.Save(profile); audioPreferencesDirty = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "Could not save audio settings: " + ex.Message; }
    }

    private void FinishAudioAdjustment()
    {
        audioVolumeCapture = null;
        if (audioPreferencesDirty) SaveAudioPreferences();
    }

    private void DrawAudioSettings(int x, int y)
    {
        canvas.Text("Audio", x, y + 7, Gold, .78f, 245, 1, true);
        MenuAction("sound", preferences.Sounds ? "Mute" : "Unmute", new(x + 264, y, 236, 40), new MatchIntent.ToggleSounds(), selected: !preferences.Sounds, size: .78f);
        canvas.Text("Master volume", x, y + 48, Ink, .75f, 355, 1);
        canvas.Text($"{preferences.Audio.Master.Volume:P0}", x + 408, y + 48, preferences.Sounds ? Gold : Muted, .75f, 92, 1, true);
        var track = new Rectangle(x + 54, y + 77, 392, 42);
        var start = new Vector2(track.Left, track.Center.Y);
        var end = new Vector2(track.Right - 1, track.Center.Y);
        var thumb = Vector2.Lerp(start, end, preferences.Audio.Master.Volume);
        bool focused = audioVolumeFocused || audioVolumeCapture != null || IsControlHovered("audio-volume");
        canvas.Line(start, end, Muted * .35f, 4);
        canvas.Line(start, thumb, preferences.Sounds ? Gold : Muted, 4);
        canvas.Circle(thumb + new Vector2(0, 2), 8, Color.Black * .65f);
        canvas.Circle(thumb, focused ? 9 : 7, focused ? Ink : preferences.Sounds ? Gold : Muted);
        var intent = new MatchIntent.SetMasterVolume(preferences.Audio.Master.Volume);
        hits.Add(new("audio-volume", ScopeFor(intent), track, intent));
        MenuAction("audio-volume-down", "−", new(x, y + 77, 42, 42), new MatchIntent.AdjustMasterVolume(-5), size: 1);
        MenuAction("audio-volume-up", "+", new(x + 458, y + 77, 42, 42), new MatchIntent.AdjustMasterVolume(5), size: 1);
    }

    private bool UpdateAudioInput(MouseState mouse, KeyboardState keys, bool blocked)
    {
        if (overlay != "settings" || blocked) {
            FinishAudioAdjustment(); audioVolumeFocused = false;
            return false;
        }
        bool press = mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released;
        if (press) {
            audioVolumeFocused = HitAt(pointer)?.Id == "audio-volume";
            if (audioVolumeFocused && HitAt(pointer) is { } track && CurrentHit(track)) {
                audioVolumeCapture = track.Bounds;
                pressed = null; dragSource = null; dragging = false; ClearCardDrag();
            }
        }
        if (audioVolumeCapture is { } bounds) {
            if (keys.IsKeyDown(Keys.Escape)) {
                FinishAudioAdjustment(); audioVolumeFocused = false; overlay = "";
            } else {
                SetMasterVolume((pointer.X - bounds.Left) / (float)Math.Max(1, bounds.Width - 1));
                if (mouse.LeftButton == ButtonState.Released) FinishAudioAdjustment();
            }
            return true;
        }
        if (!audioVolumeFocused && HitAt(pointer)?.Id != "audio-volume") return false;
        float? value = null;
        if (keys.IsKeyDown(Keys.Home) && previousKeys.IsKeyUp(Keys.Home)) value = 0;
        if (keys.IsKeyDown(Keys.End) && previousKeys.IsKeyUp(Keys.End)) value = 1;
        if (keys.IsKeyDown(Keys.Left) && previousKeys.IsKeyUp(Keys.Left)) value = preferences.Audio.Master.Volume - .05f;
        if (keys.IsKeyDown(Keys.Right) && previousKeys.IsKeyUp(Keys.Right)) value = preferences.Audio.Master.Volume + .05f;
        if (value == null) return false;
        SetMasterVolume(value.Value); FinishAudioAdjustment();
        return true;
    }
}
