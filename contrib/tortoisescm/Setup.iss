; Build through Build-Setup.ps1. The ZIP installer owns version payloads;
; Inno owns only its support files and the Windows Apps uninstall entry.
#ifndef PackageArchive
  #error PackageArchive is required
#endif
#ifndef PackageVersion
  #error PackageVersion is required
#endif
#ifndef SetupOutput
  #error SetupOutput is required
#endif
#ifndef InstalledPayloadSize
  #error InstalledPayloadSize is required
#endif

[Setup]
#ifdef TestSetup
AppId={{D7305D7A-348E-4E30-88D3-6BCE9CE856BA}
AppName=TortoiseSCM Installer Test
DefaultDirName={localappdata}\Programs\TortoiseSCM-Installer-Test
OutputBaseFilename=TortoiseSCM-{#PackageVersion}-windows-x64-Setup-Test
UninstallDisplayName=TortoiseSCM Installer Test {#PackageVersion}
#else
AppId={{B30D80DC-C4F3-4CD7-8175-86D48C0896A4}
AppName=TortoiseSCM
DefaultDirName={localappdata}\Programs\TortoiseSCM
OutputBaseFilename=TortoiseSCM-{#PackageVersion}-windows-x64-Setup
UninstallDisplayName=TortoiseSCM {#PackageVersion}
#endif
AppVersion={#PackageVersion}
AppPublisher=TortoiseSCM contributors
AppPublisherURL=https://github.com/ahpho/TortoiseSCM
AppSupportURL=https://github.com/ahpho/TortoiseSCM/issues
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
UsePreviousLanguage=no
DisableWelcomePage=no
WizardStyle=modern
SetupLogging=yes
ExtraDiskSpaceRequired={#InstalledPayloadSize}
SetupIconFile=..\..\src\Resources\gluon.ico
OutputDir={#SetupOutput}
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
Uninstallable=yes
UninstallFilesDir={app}\setup
CreateAppDir=yes
SetupMutex=TortoiseSCMSetupWrapper

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Messages]
WelcomeLabel2=本向导将安装或升级 TortoiseSCM。%n%n已包含 Beyond Compare，无需设置工具路径。需要预先安装 Plastic SCM / Unity Version Control 客户端和 .NET Framework 4.8。%n%n安装完成后使用资源管理器右键菜单。向导不会自动打开工作区，也不会自动重启资源管理器。
FinishedLabel=安装成功。%n%n请通过文件或目录的右键菜单使用 TortoiseSCM。“版本信息”可以核对当前程序版本。Beyond Compare 已随程序安装，无需设置路径。

[Files]
Source: "{#PackageArchive}"; DestName: "payload.zip"; Flags: dontcopy
Source: "SetupBridge.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "Package.Common.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "PackageExplorer.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "ModernMenu.Common.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "Uninstall.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "SetupBeyondCompare.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion

[Code]
var
  BackendInstalled, BackendRunning, WrapperInstalled: Boolean;
  ExplorerState, InstallMessage: String;
  RestartCheck: TNewCheckBox;

function Quote(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function RunBridge(const Helper, Action, Archive, ResultFile: String; var Error: String): Boolean;
var
  Arguments: String;
  ExitCode: Integer;
begin
  DeleteFile(ResultFile);
  Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + Quote(Helper) +
    ' -Action ' + Action + ' -InstallRoot ' + Quote(ExpandConstant('{app}')) + ' -ResultPath ' + Quote(ResultFile);
  if Archive <> '' then Arguments := Arguments + ' -PackageArchive ' + Quote(Archive);
#ifdef TestSetup
  Arguments := Arguments + ' -NoRegister';
#endif
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  if Result then Result := (ExitCode = 0) and (GetIniString('Result', 'Success', '0', ResultFile) = '1');
  if not Result then begin
    Error := GetIniString('Result', 'Message', '无法完成操作。请查看安装日志，修正问题后重新运行安装程序。', ResultFile);
    Log('TortoiseSCM bridge failed: ' + Error);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then SuppressibleMsgBox('请先安装 .NET Framework 4.8，然后重新运行本安装程序。', mbError, MB_OK, IDOK);
end;

procedure InitializeWizard;
begin
  RestartCheck := TNewCheckBox.Create(WizardForm);
  RestartCheck.Parent := WizardForm.FinishedPage;
  RestartCheck.SetBounds(WizardForm.FinishedLabel.Left, WizardForm.FinishedPage.ClientHeight - ScaleY(60),
    WizardForm.FinishedLabel.Width, ScaleY(36));
  RestartCheck.Caption := '现在重启资源管理器以启用新版右键菜单（将再次确认）';
  RestartCheck.Checked := False;
  RestartCheck.Visible := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultFile: String;
begin
  Result := '';
  if BackendInstalled then Exit;
#ifdef TestSetup
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{localappdata}\Programs\TortoiseSCM')) = 0 then begin
    Result := '测试安装器不能使用正式安装目录。';
    Exit;
  end;
#else
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{localappdata}\Programs\TortoiseSCM')) <> 0 then begin
    Result := '请使用默认的当前用户安装目录；不支持 /DIR 覆盖安装位置。';
    Exit;
  end;
#endif
  ExtractTemporaryFile('payload.zip');
  ExtractTemporaryFile('SetupBridge.ps1');
  ExtractTemporaryFile('Package.Common.ps1');
  ExtractTemporaryFile('PackageExplorer.ps1');
  ExtractTemporaryFile('ModernMenu.Common.ps1');
  ExtractTemporaryFile('Uninstall.ps1');
  ExtractTemporaryFile('SetupBeyondCompare.ps1');
  ResultFile := ExpandConstant('{tmp}\install-result.ini');
  BackendRunning := True;
  WizardForm.CancelButton.Enabled := False;
  try
    BackendInstalled := RunBridge(ExpandConstant('{tmp}\SetupBridge.ps1'), 'Install',
      ExpandConstant('{tmp}\payload.zip'), ResultFile, Result);
  finally
    BackendRunning := False;
    WizardForm.CancelButton.Enabled := not BackendInstalled;
  end;
  if not BackendInstalled then Exit;
  ExplorerState := GetIniString('Result', 'ExplorerState', 'Unknown', ResultFile);
  InstallMessage := GetIniString('Result', 'Message', '安装成功。', ResultFile);
  WizardForm.CancelButton.Enabled := False;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if BackendInstalled or BackendRunning then begin
    Cancel := False;
    Confirm := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then WrapperInstalled := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then begin
    WizardForm.FinishedLabel.Caption := InstallMessage + #13#10#13#10 +
      '请通过文件或目录的右键菜单使用 TortoiseSCM。“版本信息”可以核对当前程序版本。';
    if ExplorerState = 'Old' then begin
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
        '资源管理器仍加载旧版菜单。可稍后在任务管理器中重启“Windows 资源管理器”，或勾选下方选项。';
#ifndef TestSetup
      RestartCheck.Visible := True;
#endif
    end else if ExplorerState = 'Unknown' then
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
        '暂时无法判断资源管理器加载的菜单版本，请通过右键“版本信息”核对。';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if (CurPageID = wpFinished) and RestartCheck.Checked and not WizardSilent then begin
    if MsgBox('重启资源管理器可能中断正在进行的复制、移动、删除、解压等文件操作。' + #13#10#13#10 +
      '请确认这些操作已经全部结束。所有资源管理器文件夹窗口将关闭，桌面和任务栏会短暂消失。' + #13#10#13#10 +
      '是否现在重启？选择“否”可稍后处理，安装已完成。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then begin
      if not RunBridge(ExpandConstant('{app}\setup\SetupBridge.ps1'), 'RestartExplorer', '',
        ExpandConstant('{tmp}\restart-result.ini'), Error) then
        MsgBox('安装已成功，但资源管理器未能重启。' + #13#10 + Error, mbError, MB_OK);
    end;
  end;
end;

procedure DeinitializeSetup;
begin
  if BackendInstalled and not WrapperInstalled then
    SuppressibleMsgBox('TortoiseSCM 文件及右键注册步骤已完成，但安装器登记未完成。' + #13#10 +
      '有效版本已保留；请重新运行本安装程序以补齐“已安装的应用”卸载入口。', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Error: String;
begin
  { Inno 6.7.3 calls this after its confirmation and BEFORE PerformUninstall.
    An exception propagates (AllowException=False), preserving support and ARP. }
  if CurUninstallStep = usUninstall then begin
    if not RunBridge(ExpandConstant('{app}\setup\SetupBridge.ps1'), 'Uninstall', '',
      ExpandConstant('{tmp}\uninstall-result.ini'), Error) then
      RaiseException('卸载尚未完成，卸载入口已保留。请处理以下问题后重试：' + #13#10 + Error);
  end;
end;
