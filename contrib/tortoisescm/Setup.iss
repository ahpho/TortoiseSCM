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
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
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
WelcomeLabel2=本向导将安装或升级 TortoiseSCM。%n%n已包含比较和合并工具，无需另行安装。需要预先安装 Plastic SCM / Unity Version Control 客户端和 .NET Framework 4.8。%n%n安装完成后使用资源管理器右键菜单。向导不会自动打开工作区，也不会自动重启资源管理器。
FinishedLabel=安装成功。%n%n请通过文件或目录的右键菜单使用 TortoiseSCM。“版本信息”可以核对当前程序版本。默认使用包内 TortoiseGitMerge，也可在设置中选择 Beyond Compare。

[Files]
Source: "{#PackageArchive}"; DestName: "payload.zip"; Flags: dontcopy
Source: "SetupBridge.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "SetupApplications.cs"; DestDir: "{app}\setup"; Flags: ignoreversion
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
  MaintenancePage: TInputOptionWizardPage;
  InstalledRoot, InstalledUninstaller: String;
  MaintenanceFinished: Boolean;

function UninstallRegistryKey: String;
begin
#ifdef TestSetup
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D7305D7A-348E-4E30-88D3-6BCE9CE856BA}_is1';
#else
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B30D80DC-C4F3-4CD7-8175-86D48C0896A4}_is1';
#endif
end;

function Quote(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function RunBridge(const Helper, Action, Archive, ResultFile: String; var Error: String): Boolean;
var
  Arguments, Root: String;
  ExitCode: Integer;
begin
  DeleteFile(ResultFile);
  if (InstalledRoot <> '') and ((Action = 'CheckUninstall') or (Action = 'CloseUninstallApplications')) then
    Root := InstalledRoot
  else
    Root := ExpandConstant('{app}');
  Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + Quote(Helper) +
    ' -Action ' + Action + ' -InstallRoot ' + Quote(Root) + ' -ResultPath ' + Quote(ResultFile);
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
  if RegQueryStringValue(HKCU64, UninstallRegistryKey, 'InstallLocation', InstalledRoot) and (InstalledRoot <> '') then begin
    InstalledRoot := RemoveBackslashUnlessRoot(InstalledRoot);
    RegQueryStringValue(HKCU64, UninstallRegistryKey, 'UninstallString', InstalledUninstaller);
    if (Length(InstalledUninstaller) > 1) and (InstalledUninstaller[1] = '"') and
      (InstalledUninstaller[Length(InstalledUninstaller)] = '"') then
      InstalledUninstaller := Copy(InstalledUninstaller, 2, Length(InstalledUninstaller) - 2);
    MaintenancePage := CreateInputOptionPage(wpWelcome, '维护已有安装', '请选择要执行的操作',
      '已安装到：' + InstalledRoot + #13#10#13#10 +
      '升级或修复会沿用此目录。如需更换安装路径，请先卸载，再重新运行安装包。卸载会保留工作区和用户设置。', True, False);
    MaintenancePage.Add('升级 / 修复 TortoiseSCM');
    MaintenancePage.Add('卸载 TortoiseSCM');
    MaintenancePage.SelectedValueIndex := 0;
    MaintenancePage.SubCaptionLabel.ShowAccelChar := False;
    WizardForm.DirEdit.ReadOnly := True;
    WizardForm.DirBrowseButton.Enabled := False;
  end;
  RestartCheck := TNewCheckBox.Create(WizardForm);
  RestartCheck.Parent := WizardForm.FinishedPage;
  RestartCheck.SetBounds(WizardForm.FinishedLabel.Left, WizardForm.FinishedPage.ClientHeight - ScaleY(60),
    WizardForm.FinishedLabel.Width, ScaleY(36));
  RestartCheck.Caption := '现在重启资源管理器以启用新版右键菜单（将再次确认）';
  RestartCheck.Checked := True;
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
#endif
  if (InstalledRoot <> '') and (CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')), InstalledRoot) <> 0) then begin
    Result := '升级或修复必须沿用已有安装目录。如需更换路径，请先卸载，再重新运行安装包。';
    Exit;
  end;
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
  if MaintenanceFinished then begin
    Cancel := True;
    Confirm := False;
    Exit;
  end;
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
  if (CurPageID = wpSelectDir) and (InstalledRoot <> '') then begin
    WizardForm.SelectDirLabel.Caption := '升级或修复将沿用此目录。若要更换路径，请先卸载再安装。';
    WizardForm.SelectDirBrowseLabel.Caption := '现有目录不可直接修改；可返回上一步选择卸载。';
  end;
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

function TryUninstallAction(const Helper, Action, ResultFile: String): Boolean;
var
  Error, Prompt: String;
begin
  Result := RunBridge(Helper, Action, '', ResultFile, Error);
  if not Result and (GetIniString('Result', 'CanClose', '0', ResultFile) = '1') then begin
    Prompt := GetIniString('Result', 'ClosePrompt', '', ResultFile);
    StringChangeEx(Prompt, '\n', #13#10, True);
    if SuppressibleMsgBox(Prompt,
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then Exit;
    if RunBridge(Helper, 'CloseUninstallApplications', '', ResultFile, Error) then
      Result := RunBridge(Helper, Action, '', ResultFile, Error);
  end;
  if not Result then
    SuppressibleMsgBox('卸载尚未完成，卸载入口已保留。' + #13#10#13#10 + Error, mbError, MB_OK, IDOK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
  ExitCode: Integer;
  Started: Boolean;
begin
  Result := True;
  if MaintenancePage <> nil then
    if (CurPageID = MaintenancePage.ID) and (MaintenancePage.SelectedValueIndex = 1) then begin
      Result := False;
      if MsgBox('是否卸载 TortoiseSCM？' + #13#10#13#10 + InstalledRoot + #13#10#13#10 +
        '工作区和用户设置会保留。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then Exit;
      { Use only this installation's registered Inno uninstaller, never a shell command. }
      if (CompareText(ExtractFileDir(InstalledUninstaller), AddBackslash(InstalledRoot) + 'setup') <> 0) or
        (CompareText(Copy(ExtractFileName(InstalledUninstaller), 1, 5), 'unins') <> 0) or
        (CompareText(ExtractFileExt(InstalledUninstaller), '.exe') <> 0) or not FileExists(InstalledUninstaller) then begin
        MsgBox('原卸载程序缺失或安装记录无效。请先选择“升级 / 修复”，再重试卸载。', mbError, MB_OK);
        Exit;
      end;
      { Use the new preflight even when maintaining an older installed uninstaller. }
      ExtractTemporaryFile('SetupBridge.ps1');
      ExtractTemporaryFile('SetupApplications.cs');
      ExtractTemporaryFile('Package.Common.ps1');
      ExtractTemporaryFile('PackageExplorer.ps1');
      BackendRunning := True;
      WizardForm.NextButton.Enabled := False;
      WizardForm.BackButton.Enabled := False;
      WizardForm.CancelButton.Enabled := False;
      try
        if not TryUninstallAction(ExpandConstant('{tmp}\SetupBridge.ps1'), 'CheckUninstall',
          ExpandConstant('{tmp}\maintenance-result.ini')) then Exit;
        Started := Exec(InstalledUninstaller, '/VERYSILENT /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
      finally
        BackendRunning := False;
        WizardForm.NextButton.Enabled := True;
        WizardForm.BackButton.Enabled := True;
        WizardForm.CancelButton.Enabled := True;
      end;
      if Started and (ExitCode = 0) and not RegKeyExists(HKCU64, UninstallRegistryKey) then begin
        MaintenanceFinished := True;
        MsgBox('卸载成功。工作区和用户设置已保留。', mbInformation, MB_OK);
        WizardForm.Close;
      end else begin
        Log('Maintenance uninstall did not complete. Exit code: ' + IntToStr(ExitCode));
        if not Started then
          MsgBox('无法启动卸载程序，卸载入口已保留。请检查文件权限后重试。', mbError, MB_OK);
      end;
      Exit;
    end;
  if (CurPageID = wpFinished) and RestartCheck.Visible and RestartCheck.Checked and not WizardSilent then begin
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
begin
  { Inno 6.7.3 calls this after its confirmation and BEFORE PerformUninstall.
    Abort exits before file removal, preserving support and ARP without a
    misleading Pascal runtime-error dialog for expected file contention. }
  if CurUninstallStep = usUninstall then begin
    if not TryUninstallAction(ExpandConstant('{app}\setup\SetupBridge.ps1'), 'Uninstall',
      ExpandConstant('{tmp}\uninstall-result.ini')) then
      Abort;
  end;
end;
