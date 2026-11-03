; Installeur de Legendary Pen, compilé par build-release.ps1.
; Installation par utilisateur : aucun droit administrateur n'est demandé.

#ifndef AppVersion
  #error Définir AppVersion : ISCC /DAppVersion=x.y.z LegendaryPen.iss
#endif

#ifndef PublishDir
  #error Définir PublishDir, le dossier contenant LegendaryPen.exe
#endif

[Setup]
AppId={{7C1E4D52-3B6A-4F0E-9A27-5D8C2E61B3F4}
AppName=Legendary Pen
AppVersion={#AppVersion}
AppVerName=Legendary Pen {#AppVersion}
AppPublisher=Lixirian
AppPublisherURL=https://github.com/Lixirian/Legendary-Pen
DefaultDirName={localappdata}\Programs\Legendary Pen
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Legendary Pen
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=LegendaryPen-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; Une seule langue proposée d'office : celle de Windows, sans fenêtre de choix supplémentaire quand elle est disponible.
ShowLanguageDialog=auto
; Même apparence que l'application, quel que soit le thème de Windows, à l'installation comme à la désinstallation.
WizardStyle=modern dark hidebevels
WizardBackColor=#202124
WizardImageFile=images\wizard-large-100.png,images\wizard-large-150.png,images\wizard-large-200.png
WizardSmallImageFile=images\wizard-small-100.png,images\wizard-small-150.png,images\wizard-small-200.png
SetupIconFile=..\src\LegendaryPen.App\legendary-pen.ico
UninstallDisplayIcon={app}\LegendaryPen.exe
UninstallDisplayName=Legendary Pen
CloseApplications=force
RestartApplications=no

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\LegendaryPen.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Le démarrage automatique ne doit pas survivre à la désinstallation.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Legendary Pen"; Flags: dontcreatekey uninsdeletevalue

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Legendary Pen"
; Contenus privés : ils ne doivent pas rester sur le PC. Les réglages sont gardés pour une réinstallation.
Type: filesandordirs; Name: "{userappdata}\Legendary Pen\annotations"
Type: files; Name: "{userappdata}\Legendary Pen\signature.json"

[Icons]
Name: "{autoprograms}\Legendary Pen"; Filename: "{app}\LegendaryPen.exe"
Name: "{autodesktop}\Legendary Pen"; Filename: "{app}\LegendaryPen.exe"; Tasks: desktopicon

[Run]
; Installation manuelle : case à cocher en fin d'assistant.
Filename: "{app}\LegendaryPen.exe"; Description: "{cm:LaunchProgram,Legendary Pen}"; Flags: nowait postinstall skipifsilent
; Mise à jour depuis l'application : relance automatique.
Filename: "{app}\LegendaryPen.exe"; Flags: nowait; Check: WizardSilent
