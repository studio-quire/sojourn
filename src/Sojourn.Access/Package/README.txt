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

The first time you log in or teleport, Windows Firewall may ask whether Sojourn Access may use networks. Sojourn Access only makes outgoing connections, so Allow is fine, and it should work whichever you choose.

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

World sounds and the parcel's music start quieter than in other viewers, so they don't drown your screen reader. Alt+0 (settings), in the Sounds group, changes their levels.

If Sojourn Access says at login that sounds can't play because Windows' media features are missing, your Windows is an N edition, which leaves them out. Add them, then restart Sojourn Access. On Windows 11: Settings, System, Optional features, View features, then Media Feature Pack. On Windows 10: Settings, Apps, Apps and features, Optional features, Add a feature, then Media Feature Pack. Without them everything works but sound: no world sounds, music, voice chat or walk mode's sounds.

If your graphics can't run it

Sojourn Access draws the world out of sight, using your PC's graphics, to tell what's around you: walk mode's warnings and sonar, the faces of objects, and the pictures the assistant describes. On a PC whose graphics can't (a virtual machine, or a very old graphics card or driver), it says so when you use one of those, and everything else works. Updating your graphics driver may help.

[assistant]
The assistant

Sojourn Access has an AI assistant: it describes the people, things and places around you (Control+D, and Control+Shift+D for you and what you're wearing), and answers or does what you type in Alt+9. It's off until you choose how it runs, in Alt+0 (settings), in the Assistant group (Tab, then A, then Tab again):

With a token from Studio Quire, if the studio has given you one: go to the row "Key or token for the assistant", press Enter, paste the token with Control+V and press Enter. Studio Quire pays for what you use, up to a monthly allowance; if that runs out, the assistant says so and comes back on the first of the next month.

Or with your own Anthropic or OpenRouter key, put in the same way. Requests are typed; this build doesn't hear spoken requests.

What it sends: only when you ask it something, your request goes to the provider you chose, with what it looks up to answer: the names of the people and things around you and where they are, recent chat when it's needed, pictures of the view around you, and its notes on your preferences.

With a Studio Quire token or your own Anthropic key, that's Anthropic, the maker of the Claude AI models. With a token it goes through Studio Quire's relay, which passes it on and keeps nothing of it, only how much was used. With an OpenRouter key, it goes to OpenRouter and the model's provider it chooses. Your token or key is kept encrypted for your Windows account.

[/assistant]
[lean]
Without the assistant

Sojourn Access can work with an AI assistant that describes what's around you and does things for you, but this build doesn't include it. Describing (Control+D) needs the assistant, so here it tells you what Sojourn Access knows from its lists instead. Nothing else needs it.

[/lean]
Quitting

Alt+F4 in the main window. It asks "Log out and quit?": Enter quits, Escape stays.

Getting help

To report a problem or ask for something, open an issue at https://github.com/studio-quire/sojourn-access/issues, or write to hello@studioquire.org. There's no formal support and no promised response time, but both are read. Issues are public.

Saying what you pressed, what you heard and what you expected helps most.

[assistant]
If Studio Quire has given you a token, you can also reach the people making Sojourn Access from inside it:

Message the builders: Help, then Message the builders, or Control+Shift+B. It opens a conversation tab where you write to Claude sessions (AI) building Sojourn Access at Studio Quire, not a person. Replies come when a session reads your message, sometimes a day later, and are read out like an IM.

Send a log: Help, then Send a log to Studio Quire, or type //sendlog. It sends one session's log with your note on what happened. Before it goes, Review what's sent shows exactly what will be sent.

With the assistant switched on, with or without a token, you can also ask it to report a problem. It says what will be sent and waits for your key press.

[/assistant]
Your logs are in your settings folder (below). They include chat, so read one before sharing it yourself.

Where your settings live

Your settings, logs and Second Life's cache are kept in a folder of your own: Sojourn Access, inside your AppData Local folder. To open it, press Windows+R, type %LOCALAPPDATA%\Sojourn Access and press Enter. The logs of your last ten sessions are there; they include chat, and they stay on this computer unless you send one.

Privacy

Sojourn Access has no analytics or tracking, and sends no crash reports. Nothing reaches Studio Quire unless you send it.

Second Life: it connects as any viewer does, for your login, the regions you visit, their assets, search, the map and voice chat. Your username and password go only to Linden Lab, and the password isn't saved. Like every viewer, it sends Linden Lab viewer statistics: how long it has run, its memory, the regions visited, how many avatars are around, and your computer's operating system, processor and memory.

Music: if a parcel plays music and music is on, that stream's server sees your IP address, as with any viewer.

GitHub: once after you log in, and when you choose Help, then Newer build?, it asks GitHub for the newest build's number, to tell you if yours is older. Nothing of yours goes with it.

[assistant]
The assistant: only once you switch it on and ask it something, to the provider you chose, as The assistant, above, says.

Studio Quire's relay, only for what you choose to send. A log is kept 30 days. A problem report through the assistant is kept 30 days; without a token it goes anonymously, and your IP address isn't kept. Messages to the builders are kept with the replies for 90 days. While you have a token, Sojourn Access asks the relay for replies about once a minute; Alt+0's row "Messages with the builders" turns that off.

A log or report leaves out everything other people said or wrote, and their names become person 1, person 2 and so on. Keys, tokens, your Windows account's folder name and your IP address are masked too. Studio Quire never shares or sells what you send, and deletes it on request.

[/assistant]
The full privacy policy is at https://github.com/studio-quire/sojourn-access#privacy. Questions about it, or a request to delete something, go to hello@studioquire.org.

Removing it

Quit it, then delete this folder, and delete the Sojourn Access folder in AppData Local described above.[assistant] If you put in a token or key for the assistant, delete the Sojourn folder there too.[/assistant] That's everything, except that if Windows Firewall asked about it, Windows keeps your answer in its own settings, which does no harm.

Who made it

Sojourn Access is made by Claude models (AI) at Studio Quire. Blind residents trying it shape it most.

This software is not provided or supported by Linden Lab, the makers of Second Life.

Licences of the parts it includes are in the Licences folder.
