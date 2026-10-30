# Git Extensions 포크(hj) 업스트림 접점 문서

이 문서는 Git Extensions 업스트림(`upstream/master`)과의 지속적인 동기화 및 머지를 위해 포크 고유 변경 사항, 접점 파일 목록, 동기화 절차 및 개발 규칙을 기술합니다.

---

## 1. 포크 수정 업스트림 파일 목록 (Touchpoints)

다음은 업스트림 원본 파일 중 포크(hj)에서 수정한 11개 파일의 목록과 목적, 머지 시 주의사항입니다.  
(`git diff --stat upstream/master -- . ':!src/app/GitUI/ConsoleEmulation/Ghostty' ':!tests' ':!docs' ':!eng/hj'` 기준)

| 파일 경로 | 변경 내용 (무엇을) | 변경 사유 (왜) | 머지 시 주의사항 |
| :--- | :--- | :--- | :--- |
| `.gitignore` | `spike/` 항목 1줄 추가 | Ghostty 스파이크 탐색 산출물 디렉터리가 Git에 추적되지 않도록 방지 | 업스트림 `.gitignore` 변경 시 단순 추가 라인이므로 충돌 발생 가능성 낮음 |
| `Directory.Packages.props` | `LibGhostty.Net` (버전 1.0.2) `PackageVersion` 항목 추가 | Ghostty VT 파서 및 PTY 바인딩 NuGet 패키지 의존성 선언 | 업스트림 패키지 목록과 알파벳 순 정렬 유지 |
| `setup/installer/Product.wxs` | `LibGhostty.Net.dll` 컴포넌트, `runtimes/win-x64/native/*` 디렉터리 및 컴포넌트(`conpty.dll`, `ghostty-vt.dll`, `OpenConsole.exe`), `GitExtensions` Feature에 ComponentRef 등록 | 포터블 및 MSI 패키징 시 번들 일관성 검사(`_CheckBundlesConsistent`) 통과 및 Ghostty 네이티브 런타임 배포 | 업스트림 디렉터리 구조 및 Component/Feature 선언 규칙 유지 |
| `src/app/GitCommands/Settings/AppSettings.cs` | `ConsoleEmulatorName` 기본값을 `"ConEmu"`에서 `"ghostty"`로 변경 | Git Extensions 콘솔 탭의 기본 에뮬레이터를 Ghostty로 사용 | 업스트림에서 새 설정이 추가되거나 순서가 바뀌어도 기본값 `"ghostty"` 유지 확인 |
| `src/app/GitExtensions/Project.Publish.targets` | `CreateMsi` 타깃에 `Condition="'$(HjSkipMsi)' != 'true'"` 조건 속성 추가 및 `_CleanupBeforePack` 타깃에 비-Windows Ghostty 런타임(`runtimes/linux-*`, `runtimes/osx-*`) 파일·디렉터리 정리 추가 | MSVC / WiX 도구 체인이 없는 개발 머신에서 `-SkipMsi` 옵션으로 릴리스 publish 및 포터블 zip 패키징 지원 | 업스트림에서 `CreateMsi` 타깃 속성 및 `_CleanupBeforePack` 정리 항목 변경 시 포크 고유 정리 로직 유지 |
| `src/app/GitUI/CommandsDialogs/FormBrowse.cs` | `DisposeConsoleTab()` 호출, `HandleSettingsChanged`에 콘솔 설정 변경 전달 및 `ApplyConsoleSettingsIfChanged` 호출, `FillTerminalTab` 핸들러 등록, `TestAccessor`에 콘솔 속성 노출 | 콘솔 탭의 생명주기 및 설정 반영 지원. 본체 로직은 `FormBrowse.ConsoleTab.cs` partial로 분리되어 업스트림 원본 대비 diff를 44줄로 최소화 | 업스트림 FormBrowse.cs 대규모 리팩터링 시에도 1줄 호출점들만 보존하면 충돌 방지 가능 |
| `src/app/GitUI/CommandsDialogs/Menus/SettingsChangedEventArgs.cs` | `OldConsoleSettings` 속성 및 이를 받는 생성자 오버로드 추가 | 설정 대화상자 종료 후 콘솔 에뮬레이터/폰트/테마 설정 변경 여부를 감지 | 기존 2인자 생성자(`oldTranslation`, `oldCommitInfoPosition`)가 그대로 유지되므로 하위 호환성 문제 없음 |
| `src/app/GitUI/CommandsDialogs/Menus/ToolsToolStripMenuItem.cs` | 설정 창 열기 전 `FormBrowse.CaptureConsoleSettings()` 호출 후 `SettingsChangedEventArgs`에 전달 | 메뉴바를 통해 설정을 열고 닫았을 때 콘솔 변경 사항 반영 | 업스트림의 메뉴 이벤트 로직 변경 시 `CaptureConsoleSettings()` 캡처 지점 유지 |
| `src/app/GitUI/GitUI.csproj` | `<PackageReference Include="LibGhostty.Net" />` 추가 | GitUI 프로젝트에서 Ghostty 콘솔 에뮬레이터 컴파일 지원 | 업스트림 프로젝트 파일 패키지 참조 목록과 정렬 유지 |
| `src/app/GitUI/ServiceContainerRegistry.cs` | `IConsoleEmulatorsRegistry` 등록 시 `GhosttyConsoleEmulator` 인스턴스를 에뮬레이터 배열 첫 번째로 등록 | 에뮬레이터 레지스트리에 Ghostty를 최우선 에뮬레이터로 등록 | 업스트림 DI 등록 순서 변경 시 `GhosttyConsoleEmulator` 포함 유지 |
| `src/plugins/Directory.Build.props` | `ProjectReference`에 `<ExcludeAssets>native</ExcludeAssets>` ItemDefinitionGroup 추가 | 플러그인 프로젝트 빌드 시 native 런타임 dll 중복 복사 방지 | 업스트림 플러그인 빌드 속성 머지 시 해당 ItemDefinitionGroup 유지 |

---

## 2. 포크 전용 경로 목록

업스트림에는 존재하지 않으며 포크에서 독자적으로 유지 관리하는 디렉터리 및 파일 목록입니다:

- `src/app/GitUI/ConsoleEmulation/Ghostty/`: Ghostty 콘솔 에뮬레이터 핵심 구현 (`GhosttyConsoleEmulator`, `GhosttyShellRunner`, `GhosttyTerminalControl`, 렌더링/입력/선택 로직 등)
- `src/app/GitUI/CommandsDialogs/FormBrowse.ConsoleTab.cs`: `FormBrowse` partial 클래스 (콘솔 탭 생성, 해제, 설정 변경 시 재생성 로직 분리)
- `tests/app/UnitTests/GitUI.Tests/ConsoleEmulation/Ghostty/`: Ghostty 콘솔 단위 테스트 모음 (152개 테스트)
- `tests/app/IntegrationTests/UI.IntegrationTests/ConsoleEmulation/GhosttyConsoleTabTests.cs`: Ghostty 콘솔 탭 UI 통합 테스트 모음 (15개 테스트)
- `eng/hj/`: 포크 릴리스 자동화 스크립트 (`Release.ps1`, `VERSION`)
- `docs/hj/`: 포크 아키텍처 및 업스트림 접점 문서 (`UPSTREAM-TOUCHPOINTS.md`, `RELEASING.md`)

---

## 3. 업스트림 동기화 절차

업스트림의 최신 변경 사항을 포크 브랜치(`hj/main` 또는 `feature/ghostty-console`)에 병합할 때는 다음 절차를 따릅니다.

1. **업스트림 원격 저장소 최신 커밋 가져오기**:
   ```sh
   git fetch upstream
   ```
2. **로컬 master 브랜치를 upstream/master로 fast-forward 동기화**:
   ```sh
   git checkout master
   git merge --ff-only upstream/master
   ```
   *(주의: `master` 브랜치는 업스트림 미러이므로 절대 직접 커밋하지 않습니다.)*
3. **포크 작업 브랜치로 master 머지**:
   ```sh
   git checkout hj/main
   git merge master
   ```
4. **충돌 해결**:
   - 충돌은 위 **1절의 표에 기재된 11개 파일**에서만 발생해야 합니다.
   - 포크 전용 파일들은 업스트림과 겹치지 않으므로 충돌이 발생하지 않습니다.
   - 저장소에는 `rerere.enabled = true`, `merge.conflictstyle = zdiff3`가 설정되어 있어, 한 번 해결한 충돌 패턴은 자동으로 재적용됩니다.
5. **빌드 및 회귀 테스트 수행**:
   ```sh
   dotnet build /v:q
   dotnet test tests/app/UnitTests/GitUI.Tests --filter "FullyQualifiedName~Ghostty"
   dotnet test tests/app/IntegrationTests/UI.IntegrationTests --filter "FullyQualifiedName~GhosttyConsoleTab|FullyQualifiedName~FormBrowseTests"
   ```
6. **접점 표 갱신**:
   - 업스트림 변경으로 인해 수정해야 하는 업스트림 파일이 변경되었거나 추가된 경우 본 문서의 1절 표를 갱신합니다.

---

## 4. 새 기능 추가 시 3대 규칙

업스트림 머지 충돌을 영구적으로 방지하기 위해 포크에 새로운 기능을 추가할 때는 다음 3가지 규칙을 반드시 준수합니다:

1. **새 코드는 포크 전용 폴더에 작성한다**:
   - 비즈니스 로직, 렌더러, 컨트롤 등 모든 신규 구현체는 `src/app/GitUI/...` 하위의 독립 디렉터리에 배치합니다.
2. **업스트림 파일은 한 줄 단위의 최소 등록 지점만 유지한다**:
   - DI 서비스 등록(`ServiceContainerRegistry`), 메뉴 항목 추가, 이벤트 전달 등 업스트림 파일에는 1줄 수준의 연결 코드만 남깁니다.
3. **업스트림 클래스 확장 시 partial 클래스 또는 별도 파일을 사용한다**:
   - `FormBrowse`와 같이 거대한 업스트림 클래스에 기능을 추가할 때는 본체 파일(`FormBrowse.cs`)에 로직을 인라인으로 넣지 말고, 별도 partial 파일(`FormBrowse.<Feature>.cs`)을 생성하여 구현합니다.
