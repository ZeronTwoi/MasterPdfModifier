; The Windows installer of Master PDF Modifier, for Inno Setup 6. Made by .github/workflows/downloads.yml:
;
;   iscc /DAppVersion=0.1.0 /DSource=<folder with the published app> /DOutput=<folder for the installer> setup.iss
;
; It installs for the current user, so Windows does not ask for an administrator, and asks what a setup asks:
; where to, which shortcuts to make, whether to offer the app for PDF files, whether to start it.
;
; The app runs this same setup to update itself (PdfModifier/Services/Updater.cs), with /SILENT /UPDATE=1: it then
; asks nothing, waits for the app to close, installs over it with the choices made the first time and starts the
; app again.
;
; Removing the app leaves nothing of it behind: its files, its shortcuts, what it is known by to Windows, what it
; kept in the temp folder and, unless the user wants them kept, the settings and the downloaded language data.
;
; This file is UTF-8 with a byte-order mark; without the mark the compiler misreads the translated texts below.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Source
  #define Source "..\..\publish\win-x64"
#endif
#ifndef Output
  #define Output "..\..\publish"
#endif
#define AppName "Master PDF Modifier"
#define AppExe "MasterPdfModifier.exe"
#define AppMaker "Master PDF Modifier contributors"
#define AppUrl "https://github.com/ZeronTwoi/MasterPdfModifier"
; What Windows knows a PDF by when it is opened with this app.
#define FileType "MasterPdfModifier.pdf"

[Setup]
; Stays the same for every version, so that a newer one takes the place of the older.
AppId={{6E3B1D52-8C47-4F0A-9A55-2B7C1E9D4F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppMaker}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright © 2026 {#AppMaker}
; What Windows shows of the setup file itself, under Properties and when it asks whether to run it.
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppMaker}
VersionInfoCopyright=Copyright © 2026 {#AppMaker}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The app does not run on anything older, and saying so here is kinder than an app that will not start.
MinVersion=10.0
; One setup at a time.
SetupMutex=MasterPdfModifier.Setup
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\PdfModifier\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=side-*.bmp
WizardSmallImageFile=corner-*.bmp
WizardImageAlphaFormat=defined
Compression=lzma2/max
SolidCompression=yes
OutputDir={#Output}
OutputBaseFilename=MasterPdfModifier-Setup
; An app that is open is closed before its files are replaced (the wizard says so and asks). Starting it again
; is this script's business: after an update always, otherwise by the box on the last page.
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
ShowLanguageDialog=auto

; The languages the app itself speaks.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
en.StartMenuIcon=Create a Start menu shortcut
en.FileTypes=PDF files:
en.OpenWith=Offer %1 under "Open with" for PDF files
en.RemoveData=Also remove your settings and the downloaded language data?%n%nChoose No to keep them for a later installation.
es.StartMenuIcon=Crear un acceso directo en el menú Inicio
es.FileTypes=Archivos PDF:
es.OpenWith=Ofrecer %1 en «Abrir con» para los archivos PDF
es.RemoveData=¿Eliminar también sus ajustes y los datos de idioma descargados?%n%nElija No para conservarlos para una instalación posterior.
de.StartMenuIcon=Verknüpfung im Startmenü erstellen
de.FileTypes=PDF-Dateien:
de.OpenWith=%1 unter „Öffnen mit“ für PDF-Dateien anbieten
de.RemoveData=Auch Ihre Einstellungen und die heruntergeladenen Sprachdaten entfernen?%n%nMit Nein bleiben sie für eine spätere Installation erhalten.
ru.StartMenuIcon=Создать ярлык в меню «Пуск»
ru.FileTypes=Файлы PDF:
ru.OpenWith=Предлагать %1 в меню «Открыть с помощью» для файлов PDF
ru.RemoveData=Удалить также ваши настройки и загруженные языковые данные?%n%nВыберите «Нет», чтобы сохранить их для последующей установки.
fr.StartMenuIcon=Créer un raccourci dans le menu Démarrer
fr.FileTypes=Fichiers PDF :
fr.OpenWith=Proposer %1 dans « Ouvrir avec » pour les fichiers PDF
fr.RemoveData=Supprimer aussi vos paramètres et les données de langue téléchargées ?%n%nChoisissez Non pour les conserver en vue d'une installation ultérieure.
ja.StartMenuIcon=スタート メニューにショートカットを作成する
ja.FileTypes=PDF ファイル:
ja.OpenWith=PDF ファイルの「プログラムから開く」に %1 を表示する
ja.RemoveData=設定とダウンロードした言語データも削除しますか？%n%n後で再インストールするときのために残す場合は「いいえ」を選んでください。
pt.StartMenuIcon=Criar um atalho no menu Iniciar
pt.FileTypes=Ficheiros PDF:
pt.OpenWith=Propor %1 em «Abrir com» para os ficheiros PDF
pt.RemoveData=Remover também as suas definições e os dados de idioma transferidos?%n%nEscolha Não para os manter para uma instalação posterior.
tr.StartMenuIcon=Başlat menüsünde kısayol oluştur
tr.FileTypes=PDF dosyaları:
tr.OpenWith=PDF dosyaları için "Birlikte aç" altında %1 önerilsin
tr.RemoveData=Ayarlarınız ve indirilen dil verileri de kaldırılsın mı?%n%nDaha sonraki bir kurulum için saklamak isterseniz Hayır'ı seçin.

; Every shortcut is a choice. The one on the desktop is offered without its box ticked, as Windows asks of setups:
; nobody's desktop is added to unasked.
[Tasks]
Name: "startmenuicon"; Description: "{cm:StartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "openwith"; Description: "{cm:OpenWith,{#AppName}}"; GroupDescription: "{cm:FileTypes}"

; A shortcut an earlier run of the setup made goes when its box is cleared in this one.
[InstallDelete]
Type: files; Name: "{autoprograms}\{#AppName}.lnk"; Tasks: not startmenuicon
Type: files; Name: "{autodesktop}\{#AppName}.lnk"; Tasks: not desktopicon

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; The app is added to what Windows offers for a PDF. Whatever opens PDF files now goes on doing so.
[Registry]
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#FileType}"; ValueType: string; ValueName: ""; ValueData: "PDF"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#FileType}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#FileType}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "{#FileType}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; An update the app asked for ends with the app open again, on the document it had open.
Filename: "{app}\{#AppExe}"; Parameters: "{code:Reopen}"; Flags: nowait; Check: IsUpdate

; Pins the user made of the app. Windows keeps them as shortcuts of its own and would go on showing them.
[UninstallDelete]
Type: files; Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\{#AppName}.lnk"
Type: files; Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu\{#AppName}.lnk"

[Code]
const
  // The mark of an open app: a named mutex it holds for as long as it runs (AppInfo.OpenMark in the app).
  AppMark = 'MasterPdfModifier.Open';
  FileExts = 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\OpenWithProgids';
  Toasts = 'Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts';
  NameCache = 'Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache';
  Assistant = 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store';

var
  RemoveUserData: Boolean;

// True when the command line has this switch, written exactly so.
function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

// True when the app started this setup to update itself.
function IsUpdate: Boolean;
begin
  Result := HasSwitch('/UPDATE=1');
end;

// The document to open again after an update, the way a command line wants it; nothing when there was none.
function Reopen(Param: String): String;
begin
  Result := ExpandConstant('{param:REOPEN|}');
  if Result <> '' then
    Result := '"' + Result + '"';
end;

// Gives the app the time to close. True when it is closed.
function AppClosed(Seconds: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while CheckForMutexes(AppMark) and (Waited < Seconds * 10) do
  begin
    Sleep(100);
    Waited := Waited + 1;
  end;
  Result := not CheckForMutexes(AppMark);
end;

function InitializeSetup: Boolean;
begin
  // An app that updates itself starts this setup and then closes. Whatever is still open after that, or during a
  // setup that was started by hand, Setup closes itself before it replaces files (CloseApplications).
  if IsUpdate then
    AppClosed(20);
  Result := True;
end;

// What Windows offers for PDF files because of this app.
procedure RemoveOpenWith;
begin
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\Applications\{#AppExe}');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\{#FileType}');
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Classes\.pdf\OpenWithProgids', '{#FileType}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // The offer an earlier run of the setup made goes when its box is cleared in this one.
  if (CurStep = ssInstall) and not WizardIsTaskSelected('openwith') then
    RemoveOpenWith;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  // An app that is open cannot be removed whole. It gets a moment to finish closing; after that the user is asked
  // to close it, and a removal that nobody watches gives up rather than leave half an app behind.
  while not AppClosed(3) do
    if UninstallSilent or (MsgBox(FmtMessage(SetupMessage(msgUninstallAppRunningError), ['{#AppName}']), mbError, MB_OKCANCEL) <> IDOK) then
    begin
      Result := False;
      Exit;
    end;
end;

// Removes a folder with all that is in it, unless something in it is in use: then it is left whole.
procedure RemoveUnlessInUse(const Folder: String);
begin
  if DirExists(Folder) and RenameFile(Folder, Folder + '.removing') then
    DelTree(Folder + '.removing', True, True, True);
end;

// What the app keeps in the temp folder: undo histories and dragged-out pages of runs that ended badly, installers
// it fetched, and what the one-file app, if that was used here as well, unpacked of itself.
procedure RemoveScratch(const Temp: String);
begin
  if Temp = '' then
    Exit;
  DelTree(AddBackslash(Temp) + 'PdfModifier', True, True, True);
  RemoveUnlessInUse(AddBackslash(Temp) + '.net\MasterPdfModifier');
  RemoveDir(AddBackslash(Temp) + '.net');
end;

// What Windows noted about the app by itself: that it was once picked for a PDF, and its name and maker.
procedure RemoveWhatWindowsNoted;
var
  AppFile: String;
begin
  AppFile := ExpandConstant('{app}\{#AppExe}');
  RegDeleteValue(HKEY_CURRENT_USER, FileExts, '{#FileType}');
  RegDeleteValue(HKEY_CURRENT_USER, Toasts, '{#FileType}_.pdf');
  RegDeleteValue(HKEY_CURRENT_USER, Toasts, 'Applications\{#AppExe}_.pdf');
  RegDeleteValue(HKEY_CURRENT_USER, NameCache, AppFile + '.FriendlyAppName');
  RegDeleteValue(HKEY_CURRENT_USER, NameCache, AppFile + '.ApplicationCompany');
  RegDeleteValue(HKEY_CURRENT_USER, Assistant, AppFile);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Asked before anything goes, so that nothing is asked half-way. Where nobody is there to answer everything
    // goes, which is what removing an app means; /KEEPDATA says otherwise.
    if UninstallSilent then
      RemoveUserData := not HasSwitch('/KEEPDATA')
    else
      RemoveUserData := MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO) = IDYES;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    RemoveScratch(GetEnv('TEMP'));
    RemoveScratch(GetEnv('TMP'));
    RemoveWhatWindowsNoted;
    // The download of an update is never worth keeping; the rest of what the app kept is the user's to keep.
    DelTree(ExpandConstant('{localappdata}\PdfModifier\update'), True, True, True);
    if RemoveUserData then
    begin
      DelTree(ExpandConstant('{userappdata}\PdfModifier'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\PdfModifier'), True, True, True);
    end;
    RemoveDir(ExpandConstant('{localappdata}\PdfModifier'));
  end;
end;
