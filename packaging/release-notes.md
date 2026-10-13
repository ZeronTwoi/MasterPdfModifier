**New in 0.3.0**

- The app updates itself: **Update now** in the settings fetches the new version, checks it against the checksum published here and puts it in place, then opens again. Coming from 0.2.0 this happens by hand one last time: download and install as below, your settings stay.
- The Windows setup lets you choose the shortcuts it makes and whether the app is offered under "Open with". Removing the app takes its shortcuts, folders and files with it and asks whether your settings and the downloaded recognition data should go as well.
- Six more tools: *Search* (in the document or in every PDF of a folder, scans included), *Copy text and pictures*, *Extract pictures*, *Bookmarks*, *Pages per sheet* and *Check PDF/A*.
- Nothing is cut off any more: slider handles are whole, the selected page wears its ring, a long label goes over two lines instead of running out of its button or ending in dots, every tool opens at the top of its options, and the page bar stays inside a small window. In all eight languages, and at every scaling a screen is set to.

Download the file for your computer, start it, use it. No command line is involved anywhere.

**Windows** – `MasterPdfModifier-Setup.exe` installs the app: it asks where to, which shortcuts you want (Start menu, desktop) and whether to offer the app under "Open with" for PDF files, and needs no administrator. Removing it again, under *Installed apps*, leaves nothing behind. `MasterPdfModifier.exe` is the whole app in one file, for those who would rather not install anything: double-click it. Windows may say that it does not know either of them, because they are new and not signed: choose *More info*, then *Run anyway*.

**Linux** – `MasterPdfModifier.deb` for Debian, Ubuntu, Mint and their relatives: open it and let the software centre install it; the app is in the application menu afterwards. `MasterPdfModifier-Linux.tar.gz` for the others: unpack it and start `MasterPdfModifier`, which offers to put itself into the application menu.

What text recognition needs (language data, and on a fresh Windows the Microsoft Visual C++ runtime) is fetched by the app itself the first time it is used, after asking. Whether the app should tell you about new versions it asks once, at its first start; with a no it never connects for that. With a yes, a new version shows as a warning sign on the settings button, and **Update now** in the settings fetches it, checks it and puts it in place.

`SHA256SUMS.txt` lists the checksum of every download, for checking that a file is the one published here. The app does that itself before it updates.
