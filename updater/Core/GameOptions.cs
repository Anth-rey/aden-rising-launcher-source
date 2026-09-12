using System.Text;

namespace AdenRising.Updater.Core;

/// <summary>
/// The client's own settings file, as far as the launcher is concerned with it.
///
/// We do not ship this file and we do not overwrite it. It used to be seeded
/// from whichever machine built the client, which is how every player ended up
/// with one person's 1920x1080 monitor and a muted sound card -- and having the
/// launcher write the resolution on every launch would have been the same
/// mistake wearing a different hat: plenty of people start L2.exe directly, and
/// they would have found a window slightly larger than their screen with the
/// bottom edge cut off.
///
/// So the resolution and the full-screen setting the player chose in the
/// game's own Options are what decide how the game opens, whichever way they
/// start it. The launcher writes the whole file once, on a fresh install where
/// there is not one yet, and afterwards only ever corrects two values that are
/// nobody's choice: an animation switch this client gets wrong, and the
/// refresh rate of the monitor in front of the player.
/// </summary>
public static class GameOptions
{
    private static string PathIn(string gameDir) => Path.Combine(gameDir, "system", "Option.ini");

    /// <summary>
    /// Writes the file on a fresh install, and on every launch corrects the one
    /// value in it that is not the player's to get wrong.
    ///
    /// Everything else here is theirs -- resolution, full screen, and
    /// Alt+Enter between them all belong to the client, and the launcher used
    /// to steer those and broke something every time it did. These three are
    /// a different thing: not a preference somebody chose, but a switch this
    /// client gets wrong.
    ///
    /// It matters that this happens here rather than in the launcher's own
    /// head, because plenty of people start L2.exe by hand and never open the
    /// launcher again. Everybody installs through it once, though -- so a fix
    /// written into their settings file stays fixed for them either way.
    /// </summary>
    public static void Correct(string gameDir, Display.Screen screen)
    {
        var path = PathIn(gameDir);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Fresh(screen), new UTF8Encoding(false));
                return;
            }

            // Everything below this point is what the launcher used to reach in
            // and change on every launch. It changes nothing now, and the notes
            // are kept because each one records a real defect and why it stopped
            // mattering -- without them the next person re-adds the override.

            // GPUAnimation used to be forced off here. It was forced off for a
            // real reason -- animation computed on the graphics card ran fast,
            // every plant and hanging vine moving at a speed nobody chose --
            // but that no longer happens on this client, checked by playing an
            // evening with it on and watching the vegetation.
            //
            // It is not ours to set either way: the game has its own checkbox
            // for it in Options, so a player who turns it on had it turned back
            // off behind their back on the next launch, for ever. Whatever it
            // does or does not do for them, the choice is theirs.
            //
            // What it does in principle is move skinning off the processor and
            // onto the graphics card, which in this client is the one saturated
            // and the one idling respectively. That was never measured -- the
            // measurement was taken standing somewhere with no other characters
            // in sight, where there is no skinning to move.

            // The refresh rate is deliberately left alone as well. Windows
            // reports 164 for a 165 Hz screen, and writing that in produced a
            // number matching nothing the graphics wrapper offers the client --
            // which is what emptied the refresh rate in the game options and
            // made Alt+Enter throw errors, because there was no mode to switch
            // to. The wrapper enumerates the real ones; the client picks.

            // The resolution is deliberately not touched. It is the one thing
            // in here that is purely the player's choice, and it was only ever
            // clamped because the client grew its own window by a frame's width
            // on every trip through Alt+Enter -- which the graphics wrapper now
            // handles instead of the client.
        }
        catch (IOException) { }            // the game is holding it
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// What a fresh install gets, once. Deliberately not a copy of the file
    /// from the build machine: the resolution is the player's own monitor, and
    /// the sound is on -- the copy we used to ship had every volume at zero, so
    /// a new player's first impression of the game was silence.
    /// </summary>
    private static string Fresh(Display.Screen screen) => string.Join("\r\n", [
        "[Video]",
        $"GamePlayViewportX={screen.Width}",
        $"GamePlayViewportY={screen.Height}",
        "UseTrilinear=True",
        "TextureDetail=0",
        "ModelDetail=0",
        "SkipAnim=0",
        "PawnClippingRange=0",
        "TerrainClippingRange=0",
        "Gamma=0.800000",
        "PostProc=0",
        "RenderDeco=True",
        "PawnShadow=True",
        "RenderActorLimited=6",
        // The client cannot do its own anti-aliasing -- set it and the game will
        // not start at all. The graphics wrapper does it instead, from outside.
        "AntiAliasing=0",
        "ColorBits=32",
        // Left for the client and the graphics wrapper to agree on between
        // them: Windows rounds 165 Hz down to 164, and a value the wrapper
        // does not offer empties the list in the game options.
        "RefreshRate=60",
        "WeatherEffect=1",
        // Skinning on the graphics card. The default is on because in this
        // client the processor is the part that runs out and the card is the
        // part that idles -- but that is reasoning, not a measurement, and the
        // player can turn it off in the game's own Options either way.
        "GPUAnimation=True",
        "IsKeepMinFrameRate=False",
        "UseColorCursor=True",
        // Full screen from the first launch. Switching between the two at
        // runtime is off: this client crashes on the mode change, and it did so
        // through its own Direct3D, through DXVK and through dgVoodoo alike, so
        // the switch is disabled in the wrapper and the choice is made here.
        "StartupFullScreen=True",
        "",
        "[Game]",
        "HideDropItem=False",
        "ScreenShotQuality=0",
        "IsNative=True",
        "MyName=True",
        "NPCName=True",
        "PledgeMemberName=True",
        "PartyMemberName=True",
        "OtherPCName=True",
        "GroupName=True",
        "TransparencyMode=True",
        "ArrowMode=True",
        "AutoTrackingPawn=True",
        "EnterChatting=True",
        "OldChatting=True",
        "ShowZoneTitle=False",
        "ShowGameTipMsg=False",
        "IsRejectingDuel=False",
        "PartyLooting=0",
        "SystemMsgWnd=False",
        "SystemMsgWndDamage=False",
        "SystemMsgWndExpendableItem=False",
        "IsLockShortcutWnd=False",
        "Is1ExpandShortcutWnd=False",
        "Is2ExpandShortcutWnd=False",
        "IsShortcutWndVertical=False",
        "",
        "[Audio]",
        "SoundVolume=0.400000",
        "MusicVolume=0.300000",
        "WavVoiceVolume=0.400000",
        "OggVoiceVolume=0.400000",
        "AudioMuteOn=False",
        "",
        "[L2WaterEffect]",
        "EffectType=1",
        "IsUseEffect=True",
        "",
        "[ClippingRange]",
        "Terrain=8.000000",
        "Actor=6.000000",
        "StaticMesh=4.000000",
        "StaticMeshLod=6.000000",
        "Pawn=3.000000",
        "",
        "[FirstRun]",
        "FirstRun=2",
        "",
    ]);
}
