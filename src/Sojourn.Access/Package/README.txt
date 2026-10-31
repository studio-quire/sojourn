Sojourn Access, test build {VERSION}

What it is

Sojourn Access is a way to use Second Life with a screen reader and the keyboard. Its window is made of standard Windows controls. When NVDA is running, what arrives (chat, instant messages, notices) is spoken through NVDA by itself; other screen readers get it through Windows' UI Automation.

This is an early test build. Some things will be missing or wrong. Please don't rely on it for anything that matters yet.

Its double-slash commands follow Radegast's, so routes and commands written for Radegast should run here too.

Nothing is installed

Sojourn Access runs from this folder. It doesn't install anything, add a Start menu entry or change Windows settings. It doesn't need .NET or anything else installed.

Starting it

Open the folder this file is in and press Enter on SojournAccess.exe. In File Explorer, typing "sojourna" quickly moves to it (other files start with "sojourn" too).

Windows will probably warn you, because the program isn't signed. The warning is titled "Windows protected your PC". To start it anyway, Tab to the "More info" link and press Enter, then Tab to the "Run anyway" button and press Enter. NVDA may read that button as just "R".

To stop the warning, before unzipping: on the zip file, press Alt+Enter for its properties, check "Unblock" near the bottom of the General tab, press Enter, then unzip. If you've unzipped it already, do the same on SojournAccess.exe.

The first time you log in or teleport, Windows Firewall may ask whether Sojourn Access may use networks. Sojourn Access only makes outgoing connections, to Second Life, so Allow is fine, and it should work whichever you choose.

Logging in

The login window has: Grid (Second Life is chosen), Username, Password, Show password, Start at, and the Log in button. Your username is your Second Life username. Your password isn't saved; you type it each time. The login's progress is spoken as it goes. If Second Life asks you to agree to its Terms of Service or for a code from your authenticator, a small window asks you.

When you've arrived, the cursor is in the Say box, as in Radegast.

The first keys

F1: the keys, in a window to read line by line; Escape closes it. In walk mode, F1 shows walk mode's own keys.
Alt or F10: the menu bar (World, View, Read-out, Help), where everything is listed with its key.
Type //help in the Say box and press Enter: the commands.
Enter says what you typed, Shift+Enter whispers, Control+Enter shouts.
Alt+1 to Alt+9 and Alt+0: the areas (nearby chat, conversations, people, inventory, search, places, objects, notices, the assistant, settings).
Control+Tab: the next conversation.
Control+N: the newest notice (menus, offers, questions); 1 to 9, 0, minus and equals press its buttons.
Control+Shift+H: teleport home.
F3: walk mode. The arrows walk and turn, with a sound for each step and spoken warnings. Escape leaves it.
F4 held: talk in voice chat.
F6: mute what's read out, and again to turn it back on.
Control+R repeats the last thing read out; Control+1 to Control+9 read the last nine messages, 1 the newest.
//about: which version this is.

Sound

World sounds and the parcel's music start quieter than in other viewers, so they don't drown your screen reader. Alt+0 (settings) changes their levels.

If Sojourn Access says at login that sounds can't play because Windows' media features are missing, your Windows is an N edition, which leaves them out. Add them, then restart Sojourn Access. On Windows 11: Settings, System, Optional features, View features, then Media Feature Pack. On Windows 10: Settings, Apps, Apps and features, Optional features, Add a feature, then Media Feature Pack. Without them everything works but sound: no world sounds, music, voice chat or walk mode's sounds.

If your graphics can't run it

Sojourn Access draws the world out of sight, using your PC's graphics, to tell what's around you: walk mode's warnings and sonar, the faces of objects, and the pictures the assistant describes. On a PC whose graphics can't (a virtual machine, or a very old graphics card or driver), it says so when you use one of those, and everything else works. Updating your graphics driver may help.

Without the assistant

Sojourn Access can work with an AI assistant that describes what's around you and does things for you, but this build doesn't include it. Describing (Control+D) needs the assistant, so here it tells you what Sojourn Access knows from its lists instead. Nothing else needs it.

Quitting

Alt+F4 in the main window. It asks "Log out and quit?": Enter quits, Escape stays.

Where your settings live

Your settings, logs and Second Life's cache are kept in a folder of your own: Sojourn Access, inside your AppData Local folder. To open it, press Windows+R, type %LOCALAPPDATA%\Sojourn Access and press Enter. The logs of your last ten sessions are there; they include chat, and they stay on this computer.

Sojourn Access has no analytics, tracking or crash reports of its own. It connects only where any viewer does: to Second Life, and to the music stream a parcel plays.

Removing it

Quit it, then delete this folder, and delete the Sojourn Access folder in AppData Local described above. That's everything, except that if Windows Firewall asked about it, Windows keeps your answer in its own settings, which does no harm.

Who made it

Sojourn Access is made by Claude models (AI) at Studio Quire. There's no formal support: please tell the person who gave it to you what works, what doesn't, and what you'd want.

This software is not provided or supported by Linden Lab, the makers of Second Life.

Licences of the parts it includes are in the Licences folder.
