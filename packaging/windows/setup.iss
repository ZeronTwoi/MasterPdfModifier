; The Windows installer of Master PDF Modifier, for Inno Setup 6. Made by .github/workflows/downloads.yml:
;
;   iscc /DAppVersion=0.1.0 /DSource=<folder with the published app> /DOutput=<folder for the installer> setup.iss
;
; It installs for the current user, so Windows does not ask for an administrator, and asks what a setup asks:
; where to, whether to put a shortcut on the desktop, whether to offer the app for PDF files, whether to start it.
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
#define AppUrl "https://github.com/ZeronTwoi/MasterPdfModifier"
; What Windows knows a PDF by when it is opened with this app.
#define FileType "MasterPdfModifier.pdf"

[Setup]
; Stays the same for every version, so that a newer one takes the place of the older.
AppId={{6E3B1D52-8C47-4F0A-9A55-2B7C1E9D4F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Master PDF Modifier contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
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
CloseApplications=yes
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
en.FileTypes=PDF files:
en.OpenWith=Offer %1 under "Open with" for PDF files
en.RemoveData=Remove your settings and the downloaded language data as well?
es.FileTypes=Archivos PDF:
es.OpenWith=Ofrecer %1 en «Abrir con» para los archivos PDF
es.RemoveData=¿Eliminar también sus ajustes y los datos de idioma descargados?
de.FileTypes=PDF-Dateien:
de.OpenWith=%1 unter „Öffnen mit“ für PDF-Dateien anbieten
de.RemoveData=Auch Ihre Einstellungen und die heruntergeladenen Sprachdaten entfernen?
ru.FileTypes=Файлы PDF:
ru.OpenWith=Предлагать %1 в меню «Открыть с помощью» для файлов PDF
ru.RemoveData=Удалить также ваши настройки и загруженные языковые данные?
fr.FileTypes=Fichiers PDF :
fr.OpenWith=Proposer %1 dans « Ouvrir avec » pour les fichiers PDF
fr.RemoveData=Supprimer aussi vos paramètres et les données de langue téléchargées ?
ja.FileTypes=PDF ファイル:
ja.OpenWith=PDF ファイルの「プログラムから開く」に %1 を表示する
ja.RemoveData=設定とダウンロードした言語データも削除しますか？
pt.FileTypes=Ficheiros PDF:
pt.OpenWith=Propor %1 em «Abrir com» para os ficheiros PDF
pt.RemoveData=Remover também as suas definições e os dados de idioma transferidos?
tr.FileTypes=PDF dosyaları:
tr.OpenWith=PDF dosyaları için "Birlikte aç" altında %1 önerilsin
tr.RemoveData=Ayarlarınız ve indirilen dil verileri de kaldırılsın mı?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "openwith"; Description: "{cm:OpenWith,{#AppName}}"; GroupDescription: "{cm:FileTypes}"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
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

[Code]
// When the app is removed: what it kept for its user (settings, language data) stays unless they say otherwise.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\PdfModifier'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\PdfModifier'), True, True, True);
    end;
end;
