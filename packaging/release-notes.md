**New in 0.2.0**

- *Cut sheet into pages* leaves out what lies around a page: the dark edge of the scanner, the rim of pages under an open book, the shadow in its fold, the black along a photocopy. A slider sets how close to the print the cut goes; strips of a set width can be taken off by hand instead. An edge that is not certain is marked red, on the sheet and in the page list.
- *Rotate pages* is buttons that act at once (90° left, 90° right, 180°, mirrored left to right or top to bottom). It also finds the pages that lie on their side or stand on their head, shows each the way it will stand, and sets them all upright.
- The settings of every tool are quicker to set: choices are buttons that are all on view, sliders offer the values wanted most, where a page number or a watermark goes is clicked on a small picture of a page, crop shows a frame that is pulled into place, and every tool says which pages it is going to work on.

Download the file for your computer, start it, use it. No command line is involved anywhere.

**Windows** – `MasterPdfModifier-Setup.exe` installs the app: it asks where to, which shortcuts you want (Start menu, desktop) and whether to offer the app under "Open with" for PDF files, and needs no administrator. Removing it again, under *Installed apps*, leaves nothing behind. `MasterPdfModifier.exe` is the whole app in one file, for those who would rather not install anything: double-click it. Windows may say that it does not know either of them, because they are new and not signed: choose *More info*, then *Run anyway*.

**Linux** – `MasterPdfModifier.deb` for Debian, Ubuntu, Mint and their relatives: open it and let the software centre install it; the app is in the application menu afterwards. `MasterPdfModifier-Linux.tar.gz` for the others: unpack it and start `MasterPdfModifier`, which offers to put itself into the application menu.

What text recognition needs (language data, and on a fresh Windows the Microsoft Visual C++ runtime) is fetched by the app itself the first time it is used, after asking. Whether the app should tell you about new versions it asks once, at its first start; with a no it never connects for that. With a yes, a new version shows as a warning sign on the settings button, and **Update now** in the settings fetches it, checks it and puts it in place.

`SHA256SUMS.txt` lists the checksum of every download, for checking that a file is the one published here. The app does that itself before it updates.
