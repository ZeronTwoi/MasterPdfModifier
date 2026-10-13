# PDF Modifier: the guide

What each part of the app does, how to get around in it, and the limits worth knowing.
The short version is on the [main page](../README.md).

- [The window](#the-window)
- [Typed pages and scans](#typed-pages-and-scans)
- [Edit text and images](#edit-text-and-images)
- [Cut sheet into pages](#cut-sheet-into-pages)
- [The page list](#the-page-list)
- [Poor scans](#poor-scans)
- [Compress](#compress)
- [Automate](#automate)
- [Languages](#languages)
- [Getting around](#getting-around)
- [Good to know](#good-to-know)

## The window

- **Left:** the tools, in four groups (Edit, Pages, Optimise, Review and protect). The box above them finds a tool
  by a word of its name or of what it does.
- **Next to them:** the page list. Pages are selected, reordered, added and removed here.
- **Middle:** the page on view, with the bar for turning pages and zooming under it.
- **Right:** the settings of the tool that is selected, and its **Apply** button.
- **Top:** undo and redo, settings, Open, Save as and Save. A dot next to the file name says there are changes that
  are not saved yet.

## Typed pages and scans

**Pages are text, pictures of text, or both, and the tools work on all three.** A page that is a scan is read by
text recognition; a scan that already carries a search layer brings its text along; a page that mixes a scan with
typed text (a reference number stamped on it, a typed header) has both kinds offered side by side. The same goes for
the other tools: watermarks are removed whether they are objects laid over the page or part of the scanned picture,
"make searchable" adds only what is not text already, clean-up handles scans stored as one picture or in strips.

**You see what a tool will do before it does it.** Watermarks, page numbers, rotation, scaling, compression,
clean-up and watermark removal are shown on the page as you change their settings, marked as a preview; cropping
shows the part that will be kept. Nothing changes in the document until you press Apply, and everything can be
undone.

## Edit text and images

<img src="images/edit-scan.png" width="820" alt="A scanned page with every line of text found and outlined">

This tool is an editor, not a form:

- Text the page has is highlighted: blue for text of the document, orange for text found in the page image. Click a
  line, type, apply. In a scan the old line is painted over in the colour of the paper and the new text set in a
  matching size and colour.
- New text goes into a box you draw on the page. Font, size, bold, italic, colour and alignment can be set before or
  after; the box can be moved, resized, turned and typed into again at any time.
- Images are placed by dragging out an area or with a click. Images used before are offered again under the Browse
  button; with the pointer on one (or a right-click) it can be pinned, so it stays however many others follow, or
  taken off the list. They can be moved, resized, turned and faded.
- The eraser is told what it may touch: the page itself, text added afterwards, images added afterwards. The page is
  painted over in the colour around the area or in one you choose.
- What was added stays an object: it can be changed again later, also after the file was saved and reopened here.

## Cut sheet into pages

<img src="images/cut-sheet.png" width="820" alt="Four pages found on one scanned sheet, each framed and numbered">

For a scan or photograph that shows two, four or more pages at once: an open book, pages lying next to each other on
a table, a print with several pages per sheet.

- The pages are found by the paper standing out from what it lies on, by the empty bands between them, or by the
  shadow of a fold. Each is framed on the sheet and numbered with the place it will get.
- Their order follows the page numbers printed on them, which are read from the picture. Pages without a number stay
  where reading order puts them; without any numbers the order is left to right, top to bottom. It can also be set
  to right to left or column by column, or by clicking the pages one after the other.
- Every frame can be moved, resized or removed, and frames can be drawn by hand. When nothing is found the sheet can
  be divided evenly instead (two across, two down, 2 × 2, or any grid).
- Cutting gives each page what is on its part of the sheet and nothing else: its text, and of a picture only the
  pixels that show. Several sheets are cut in one go.
- A sheet is only divided when enough speaks for it: a band that is wide for the page, stands out among the other
  gaps on it and matches the margins around it. Gaps between paragraphs do not make two pages of one. How much is
  needed is set with the **detection limit**. Where the outcome is a close call, the frames are red and so is the
  mark the sheet gets in the page list, so one look down the list shows what to check. A doubtful sheet can be
  corrected, confirmed as right, left out, or simply taken out of the selection.

<img src="images/sheets-in-doubt.png" width="820" alt="Three sheets in the page list, two of them marked red because the division is not certain">

## The page list

The page list is where pages come and go.

- Drop pictures (PNG, JPEG, WebP, BMP, GIF) or PDF files into it and they become pages at the place they are dropped;
  dropped anywhere into an empty window they become the document. A JPEG is stored as it is, without being
  compressed a second time.
- Drag pages within the list to put them in another order: one page, or all that are selected.
- Drag a page out of the window, onto a folder or the desktop, and it arrives there as a PNG picture.
- Delete removes the selected pages; the buttons under the list and the right-click menu also turn them and save
  them as pictures.

## Poor scans

<img src="images/improve-clarity.png" width="820" alt="A blurry scan brought into focus, shown as a preview">

Poor scans get more help than good ones need:

- *Improve clarity* starts from what is wrong with the pages: an ordinary scan, blurry print, faint or broken print,
  a photograph with shadows. Each choice sets the single steps, which can then be adjusted with the page on view
  showing the result. Blurry print is brought back into focus rather than merely sharpened, thin print is made
  heavier, uneven light is evened out in colour, and coarse scans can be doubled in resolution first.
- Lines that text recognition more guessed at than read are shown in red, in the editor and in *Make searchable*.
- *Read scans carefully* reads every page several ways (focused and enlarged, in pure black and white) and keeps the
  surest reading of each line. It is several times slower and meant for scans that need it.
- A passage that came out wrong can be marked and is then read on its own, with every strength of focusing tried:
  *Read an area more carefully* in the editor, *Mark a passage* in *Make searchable*.

<img src="images/doubt-in-red.png" width="820" alt="The same blurry scan in the editor, the lines that were hard to read outlined in red">

## Compress

Compress works on the open document, with the page on view showing what the images will look like, or on a whole
folder. For a folder, *Measure only* lists what every file would weigh without writing anything; the run itself
writes the compressed files into a folder of their own (or in place of the originals, after asking) and lists the
sizes before and after, file by file and in total.

<img src="images/compress-folder.png" width="820" alt="A folder measured for compression: the size of every file before and after, and the total">

## Automate

Automate runs a chain of steps (improve clarity, remove watermarks, crop to content, flatten, OCR, compress, remove
metadata) on the selected or all pages of the open document, or on a batch of files.

## Languages

The interface is available in English, Español, Deutsch, Русский, Français, 日本語, Português and Türkçe (English
by default; change it under **Settings**, the button with the sliders at the top).

**Detection languages**, the ones text recognition looks for in scans, are chosen in the same place; several can be
ticked at once, and the language of each page is then worked out automatically. Language data (2–15 MB per language)
is downloaded on request from the official Tesseract repository, from Settings or right where it is first needed.

<img src="images/settings-turkish.png" width="820" alt="The settings, with the interface in Turkish and the list of detection languages">

## Getting around

| To | Do this |
| --- | --- |
| Turn pages | ← / →, Page Up / Page Down, Home / End, or keep scrolling past the top or bottom of a page |
| Zoom | Ctrl + wheel (around the pointer), Ctrl + plus / minus, Ctrl + 1 for 100 %, *Fit page*, *Fit width* |
| Move the page | Drag it, drag with the middle button, or hold Space and drag |
| Move an object | Drag it; the arrow keys move it by a point (with Shift by ten) |
| Resize and turn an object | Pull a handle; the round handle turns (with Shift in steps of 15°) |
| Remove an object | Delete |
| Copy an object | Ctrl + D |
| Find a tool | Type a word of its name, or of what it does, into the box above the tool list |
| Add pages | Drop pictures or PDF files into the page list, or use the + above it |
| Reorder pages | Drag them in the page list |
| Get a page as a picture | Drag it out of the window |
| Delete pages | Select them in the page list and press Delete |
| Open, save, save as | Ctrl + O, Ctrl + S, Ctrl + Shift + S |
| Undo, redo | Ctrl + Z, Ctrl + Y |

## Good to know

- Text of the document is edited line by line. Text that follows on the same line moves along; paragraphs are not
  re-wrapped. A line can be turned into a text box, which can then be moved and restyled freely.
- When a replacement needs characters the document's own (usually cut-down) font lacks, a matching installed font is
  embedded instead, so the look can differ slightly.
- Text inside nested graphics (some programs wrap whole pages that way) cannot be changed in place. It is found all
  the same and replaced the way text in a scan is: painted over and retyped.
- Removing a mark from a scan changes the picture of the page. Marks lighter than the text and marks in a colour of
  their own come out cleanly, also where they cross text; a black mark printed over black text cannot be told from
  it.
- Pages on a sheet are cut out straight. One that lies askew in a photograph comes with the corners of what was
  around it; *Improve clarity* straightens it afterwards.
- Page numbers are read by text recognition, so they need its language data (Settings). Without it the pages of a
  sheet are put in reading order.
- Bringing print into focus helps where the letters are blurred but still there; how much is right depends on the
  blur, so the slider is worth trying on the page on view. Print that has dissolved altogether stays unreadable.
- Compression gains come from images. Text-only documents are already compact.
- Merge, split and extract build new documents, so bookmarks are not carried over. In-place tools keep them.
