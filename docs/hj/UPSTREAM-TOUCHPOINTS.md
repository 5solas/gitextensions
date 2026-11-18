# Git Extensions 포크(hj) 업스트림 접점 문서

이 문서는 Git Extensions 업스트림(`upstream/master`)과의 지속적인 동기화 및 패치 스택 관리를 위해 포크 고유 변경 사항, 접점 파일 목록, 동기화 절차 및 개발 규칙을 기술합니다.

---

## 1. 포크 수정 업스트림 파일 목록 (Touchpoints)

다음은 업스트림 원본 파일 중 포크(hj)의 토픽 브랜치(`feature/ghostty-console`, `feature/perf-repo-open`, `hj/infra`)에서 수정한 업스트림 파일 목록과 목적, 머지 시 주의사항입니다.  
(`src`, `setup`, 루트 설정 파일 기준)

| 파일 경로 | 소속 토픽 | 변경 내용 (무엇을) | 변경 사유 (왜) | 머지 시 주의사항 |
| :--- | :--- | :--- | :--- | :--- |
| `.gitignore` | `hj/infra` | `spike/` 항목 1줄 추가 | Ghostty 스파이크 탐색 산출물 디렉터리가 Git에 추적되지 않도록 방지 | 업스트림 `.gitignore` 변경 시 단순 추가 라인이므로 충돌 가능성 낮음 |
| `Directory.Packages.props` | `feature/ghostty-console` | `LibGhostty.Net` (버전 1.0.2) `PackageVersion` 항목 추가 | Ghostty VT 파서 및 PTY 바인딩 NuGet 패키지 의존성 선언 | 업스트림 패키지 목록과 알파벳 순 정렬 유지 |
| `setup/installer/Product.wxs` | `feature/ghostty-console` | `LibGhostty.Net.dll` 컴포넌트, `runtimes/win-x64/native/*` 디렉터리 및 컴포넌트(`conpty.dll`, `ghostty-vt.dll`, `OpenConsole.exe`), `GitExtensions` Feature에 ComponentRef 등록 | 포터블 및 MSI 패키징 시 번들 일관성 검사(`_CheckBundlesConsistent`) 통과 및 Ghostty 네이티브 런타임 배포 | 업스트림 디렉터리 구조 및 Component/Feature 선언 규칙 유지 |
| `src/app/GitCommands/Git/Commands.Arguments.cs` | `feature/perf-repo-open` | `GetRefs`에 `withTaggerDate` 매개변수 및 태거 날짜 포맷(`taggerdate:unix`) 추가 | CommitInfo에서 태그 정렬 시 추가 git 프로세스를 띄우지 않고 refs 파싱 시 날짜 획득 | 업스트림 `GetRefs` 서명 및 ref format 변경 시 전달 인자 및 포맷 보존 |
| `src/app/GitCommands/Git/Commands.Execution.cs` | `feature/perf-repo-open` | `GetGitSettingsWithScope`, `SupportConfigShowScope` 메서드 추가 | `git config --show-scope --null`로 1회 호출하여 모든 설정과 스코프를 일괄 조회 | 업스트림 config 실행 헬퍼 변경 시 스코프 조회 헬퍼 보존 |
| `src/app/GitCommands/Git/GitModule.cs` | `feature/perf-repo-open` | `ConfigSnapshotProvider` 연동, `InvalidateGitSettings`에 스냅샷 무효화 추가, `GetRemoteNames`/`GetRemotesAsync`에서 스냅샷 우선 조회, `ParseRefs`에서 `ParseRefNameAndTaggerDate` 호출 | 리포지토리 오픈 시 중복 config 및 remote git 프로세스 호출 제거, 태그 날짜 파싱 | 업스트림 GitModule 메서드 변경 시 스냅샷 우선 조회 훅 유지 |
| `src/app/GitCommands/Git/GitRef.cs` | `feature/perf-repo-open` | `TaggerDate` 속성 및 생성자 오버로드 추가 | 태그 날짜 정보를 ref 객체에 보관하여 후속 git 프로세스 호출 방지 | 업스트림 GitRef 생성자 서명 변경 시 `TaggerDate` 속성 보존 |
| `src/app/GitCommands/Settings/AppSettings.cs` | `feature/ghostty-console` | `ConsoleEmulatorName` 기본값을 `"ConEmu"`에서 `"ghostty"`로 변경 | Git Extensions 콘솔 탭의 기본 에뮬레이터를 Ghostty로 사용 | 업스트림에서 새 설정이 추가되거나 순서가 바뀌어도 기본값 `"ghostty"` 유지 확인 |
| `src/app/GitCommands/Settings/EffectiveGitConfigSettings.cs` | `feature/perf-repo-open` | 생성자에 `GitConfigSnapshotProvider` 전달 및 `GetValue`에서 스냅샷 우선 조회 | Effective 설정 조회 시 스냅샷 활용으로 프로세스 생성 방지 | 업스트림 GetValue 구현 변경 시 스냅샷 우선 조회 분기 유지 |
| `src/app/GitCommands/Settings/GitConfigSettings.cs` | `feature/perf-repo-open` | 생성자에 `GitConfigSnapshotProvider` 전달, `GetValue`/`GetValues`/`SetValue` 등에서 partial 메서드 호출 훅 연결 | Local 설정 조회/수정 시 스냅샷 활용 | 업스트림 설정 입출력 변경 시 partial 호출 연결 유지 |
| `src/app/GitCommands/Settings/GitConfigSettingsBase.cs` | `feature/perf-repo-open` | `SnapshotProvider` protected 속성 및 생성자 매개변수 추가 | 설정 클래스 전반에서 스냅샷 인스턴스 공유 | 업스트림 베이스 클래스 생성자 변경 시 필드 보존 |
| `src/app/GitExtensions.Extensibility/Git/FilteredGitRefsProvider.cs` | `feature/perf-repo-open` | `GetSortedTags(IGitModule)`에서 `IGitModule.GetRefs` 호출 시 `withTaggerDate: true` 전달 | 정렬된 태그 목록 조회 시 태그 날짜 포함 | 업스트림 Provider 로직 변경 시 전달 인자 확인 |
| `src/app/GitExtensions.Extensibility/Git/IGitModule.cs` | `feature/perf-repo-open` | `GetRemoteNames()`, `GetRemotesAsync()` 인터페이스 계약 보존 | 외부/내부 모듈 인터페이스 계약 | 인터페이스 변경 시 메서드 시그니처 유지 |
| `src/app/GitExtensions/Project.Publish.targets` | `hj/infra` | `CreateMsi` 타깃에 `Condition="'$(HjSkipMsi)' != 'true'"` 조건 추가 및 `_CleanupBeforePack` 타깃에 비-Windows Ghostty 런타임 정리 추가 | MSVC / WiX 도구 체인이 없는 개발 머신에서 `-SkipMsi` 옵션으로 릴리스 publish 및 포터블 zip 패키징 지원 | 업스트림에서 `CreateMsi` 및 `_CleanupBeforePack` 항목 변경 시 포크 고유 로직 유지 |
| `src/app/GitUI/CommandsDialogs/FormBrowse.InitRevisionGrid.cs` | `feature/perf-repo-open` | `_repoObjectsTree.Initialize` 및 `CommitInfo`에 refs 및 stash 공급 람다 전달 | 리비전 그리드 초기화 시 이미 로드된 refs를 재사용하여 중복 조회 방지 | 업스트림 그리드 초기화 체인 머지 시 전달자 보존 |
| `src/app/GitUI/CommandsDialogs/FormBrowse.cs` | `feature/ghostty-console`, `feature/perf-repo-open` | 콘솔 탭 생명주기(`DisposeConsoleTab`), 설정 반영(`ApplyConsoleSettingsIfChanged`), 터미널 채우기 등록 및 리비전 선택 시 CommitInfo/좌측 패널로 refs 전달 | 콘솔 탭 생명주기 지원 및 브라우즈 폼 메인 이벤트에서 중복 git 호출 억제 | 본체 로직은 partial로 분리되어 있으므로 1줄 호출점들만 보존 |
| `src/app/GitUI/CommandsDialogs/Menus/SettingsChangedEventArgs.cs` | `feature/ghostty-console` | `OldConsoleSettings` 속성 및 이를 받는 생성자 오버로드 추가 | 설정 대화상자 종료 후 콘솔 에뮬레이터/폰트/테마 설정 변경 여부를 감지 | 기존 2인자 생성자가 유지되므로 하위 호환성 문제 없음 |
| `src/app/GitUI/CommandsDialogs/Menus/ToolsToolStripMenuItem.cs` | `feature/ghostty-console` | 설정 창 열기 전 `FormBrowse.CaptureConsoleSettings()` 호출 후 `SettingsChangedEventArgs`에 전달 | 메뉴바를 통해 설정을 열고 닫았을 때 콘솔 변경 사항 반영 | 업스트림 메뉴 이벤트 로직 변경 시 `CaptureConsoleSettings()` 호출점 유지 |
| `src/app/GitUI/CommitInfo/CommitInfo.cs` | `feature/perf-repo-open` | `_tagsOrderDict`, `_refsSource`, `_refsProvidedByHost` 필드 및 `TryGetSortedTagsFromRefs` partial 연결 | 커밋 정보 패널에서 호스트 제공 refs로부터 태그 순서를 인메모리 구성하여 추가 git 호출 제거 | 업스트림 CommitInfo 렌더링/로딩 변경 시 캐시 적용 확인 |
| `src/app/GitUI/GitUI.csproj` | `feature/ghostty-console` | `<PackageReference Include="LibGhostty.Net" />` 추가 | GitUI 프로젝트에서 Ghostty 콘솔 에뮬레이터 컴파일 지원 | 업스트림 프로젝트 파일 패키지 참조 목록과 정렬 유지 |
| `src/app/GitUI/LeftPanel/RepoObjectsTree.cs` | `feature/perf-repo-open` | `RefreshRevisionsLoading`, `SelectionChanged`, `OnUICommandsSourceSet`에서 캐시 초기화 및 `TryBeginMergedBranchesUpdate` 호출 | 좌측 트리 탭 전환이나 동일 커밋 재선택 시 불필요한 머지 브랜치 판별 git 프로세스 호출 차단 | 업스트림 좌측 패널 선택 이벤트 변경 시 캐시 검사 훅 유지 |
| `src/app/GitUI/ServiceContainerRegistry.cs` | `feature/ghostty-console` | `IConsoleEmulatorsRegistry` 등록 시 `GhosttyConsoleEmulator` 인스턴스를 에뮬레이터 배열 첫 번째로 등록 | 에뮬레이터 레지스트리에 Ghostty를 최우선 에뮬레이터로 등록 | 업스트림 DI 등록 순서 변경 시 `GhosttyConsoleEmulator` 포함 유지 |
| `src/plugins/Directory.Build.props` | `hj/infra` | `ProjectReference`에 `<ExcludeAssets>native</ExcludeAssets>` ItemDefinitionGroup 추가 | 플러그인 프로젝트 빌드 시 native 런타임 dll 중복 복사 방지 | 업스트림 플러그인 빌드 속성 머지 시 해당 ItemDefinitionGroup 유지 |

---

## 2. 포크 전용 경로 목록

업스트림에는 존재하지 않으며 포크에서 독자적으로 유지 관리하는 디렉터리 및 파일 목록입니다:

- **Ghostty 콘솔 구현체 및 테스트**:
  - `src/app/GitUI/ConsoleEmulation/Ghostty/`: Ghostty 콘솔 에뮬레이터 핵심 구현 (`GhosttyConsoleEmulator`, `GhosttyShellRunner`, `GhosttyTerminalControl`, 렌더링/입력/선택 로직 등)
  - `src/app/GitUI/CommandsDialogs/FormBrowse.ConsoleTab.cs`: `FormBrowse` partial 클래스 (콘솔 탭 생성, 해제, 설정 변경 시 재생성 로직 분리)
  - `tests/app/UnitTests/GitUI.Tests/ConsoleEmulation/Ghostty/`: Ghostty 콘솔 단위 테스트 모음
  - `tests/app/IntegrationTests/UI.IntegrationTests/ConsoleEmulation/GhosttyConsoleTabTests.cs`: Ghostty 콘솔 탭 UI 통합 테스트 모음
- **리포지토리 오픈 성능 개선 구현체 및 테스트**:
  - `src/app/GitCommands/Git/GitModule.ConfigSnapshot.cs`: `GitModule` partial 클래스 (config 스냅샷 기반 remote 캐시 조회)
  - `src/app/GitCommands/Git/GitModule.Refs.cs`: `GitModule` partial 클래스 (ref 파싱 및 태거 날짜 추출)
  - `src/app/GitCommands/Settings/GitConfigSettings.Snapshot.cs`: `GitConfigSettings` partial 클래스 (스냅샷 기반 로컬 설정 읽기/쓰기)
  - `src/app/GitCommands/Settings/GitConfigSnapshot.cs`: 파싱된 Git 설정 스냅샷 모델
  - `src/app/GitCommands/Settings/GitConfigSnapshotProvider.cs`: 설정 스냅샷 수명주기 및 지연 로딩 프로바이더
  - `src/app/GitUI/CommitInfo/CommitInfo.SortedTags.cs`: `CommitInfo` partial 클래스 (호스트 refs 기반 태그 정렬 캐시)
  - `src/app/GitUI/LeftPanel/RepoObjectsTree.MergedBranchesCache.cs`: `RepoObjectsTree` partial 클래스 (머지 브랜치 판별 중복 호출 캐시)
  - `tests/app/IntegrationTests/UI.IntegrationTests/CommandsDialogs/FormBrowseGitProcessBudgetTests.cs`: 리포지토리 오픈 git 프로세스 예산 통합 테스트
  - `tests/app/UnitTests/GitCommands.Tests/Settings/GitConfigSnapshotTests.cs`: Git 설정 스냅샷 단위 테스트
- **포크 인프라 및 자동화 도구**:
  - `eng/hj/Sync-Upstream.ps1`: 업스트림 동기화 자동화 스크립트 (패치 스택 rebase 및 머지)
  - `eng/hj/topics.txt`: `hj/main`에 순서대로 머지할 토픽 브랜치 목록
  - `eng/hj/Release.ps1`: 포크 릴리스 자동화 스크립트
  - `eng/hj/VERSION`: 포크 릴리스 빌드 번호 카운터
  - `eng/hj/Measure-RepoOpen.ps1`: 리포지토리 오픈 성능 측정 하네스
  - `docs/hj/`: 포크 아키텍처, 성능 분석 및 업스트림 접점 문서

---

## 3. 업스트림 동기화 절차 (패치 스택 방식)

포크 `5solas/gitextensions`는 업스트림 변경 사항을 깨끗하게 추종하고 각 기능 PR의 독립성을 유지하기 위해 **패치 스택(Patch Stack)** 브랜치 모델을 사용합니다.

### 3.1 브랜치 역할 및 저장소 설정

- **`master`**: 업스트림(`upstream/master`)의 1:1 미러 브랜치입니다. 로컬이나 origin에서 `master`에 직접 커밋하는 것은 절대 금지됩니다.
- **토픽 브랜치 (`topics.txt`)**: `master` 위에 쌓인 독립적인 기능 커밋들로, 각 기능은 업스트림 PR 단위로 관리됩니다:
  - `feature/ghostty-console`: Ghostty 콘솔 에뮬레이터 구현체
  - `feature/perf-repo-open`: 리포지토리 오픈 git 프로세스 예산 최적화
  - `hj/infra`: 포크 고유 인프라 (`eng/hj/*`, `docs/hj/*`, 패키징 조건부 설정 등)
- **`hj/main`**: 동기화할 때마다 `master`에서 새로 만들어 토픽 브랜치들을 순서대로 머지(`git merge --no-ff`)하는 통합 브랜치입니다. 릴리스 태그(`hj/v<ver>`)는 이 `hj/main` 커밋에 붙습니다.
- **저장소 자동 충돌 해결 설정**:
  - `git config rerere.enabled true`: 이미 한 번 수동으로 해결한 충돌 패턴을 기억하여 자동 재적용합니다.
  - `git config merge.conflictstyle zdiff3`: 충돌 발생 시 원본 공통 조상 베이스(base)를 함께 보여주어 정확한 충돌 해결을 돕습니다.

### 3.2 `Sync-Upstream.ps1` 스크립트를 통한 동기화

평상시 업스트림 동기화는 자동화 스크립트를 실행하여 수행합니다.

```pwsh
# 1. 일상적인 동기화 (fetch -> master ff -> 토픽 rebase -> hj/main merge -> build -> tests)
pwsh eng/hj/Sync-Upstream.ps1

# 2. 시험 실행 (실제 브랜치 변경 없이 임시 브랜치 tmp/sync-*에서 rebase/merge 가능 여부만 검증)
pwsh eng/hj/Sync-Upstream.ps1 -DryRun -SkipBuild -SkipTests

# 3. 모든 검증 통과 후 원격(origin)에 푸시까지 완료
pwsh eng/hj/Sync-Upstream.ps1 -Push

# 4. 특정 토픽 파일 지정 (기본값은 hj/infra 브랜치의 eng/hj/topics.txt)
pwsh eng/hj/Sync-Upstream.ps1 -Topics <path>
```

스크립트는 내부적으로 다음 과정을 수행합니다:
1. 작업 트리가 깨끗한지 확인하고 다른 워크트리와의 브랜치 충돌을 사전 차단하며, 브랜치 전환 시 스크립트 잠금 및 유실을 방지하기 위해 임시 폴더로 자체 복사 후 재실행(re-exec)합니다. 시작 전 모든 대상 브랜치(master, 토픽들, hj/main)의 SHA를 `refs/hj-sync-backup/<timestamp>/<branch>`에 백업합니다.
2. `git fetch upstream --prune --tags` 및 `git fetch origin --prune`를 실행합니다.
3. `master`를 `upstream/master`로 fast-forward 동기화합니다 (직접 커밋이 발견되어 ff가 불가능하면 즉시 중단).
4. 토픽 목록 브랜치들을 순서대로 `master` 위로 rebase합니다(`-Topics` 미지정 시 `hj/infra` 브랜치의 `eng/hj/topics.txt`에서 읽으며, 부재 시 작업 트리 파일로 폴백). 업스트림에 이미 채택되어 내용이 같아진 커밋은 건너뛰며, rebase 완료 후 조상 여부 및 커밋 수 일관성을 자동 검증합니다.
5. `hj/main`을 안전하게 재생성(`git checkout -B hj/main master`, `git reset --hard` 미사용)한 후 각 토픽 브랜치를 `--no-ff`로 머지하고, 첫 부모 체인(first-parent)에 토픽 머지 커밋이 올바르게 존재하는지 검증합니다.
6. 솔루션 빌드(`dotnet build /v:q`) 및 주요 회귀 테스트를 순차 실행합니다.
7. 변경 요약(각 토픽 전/후 SHA, 커밋 수, 건너뛴 커밋 목록, `hj/main` SHA, 복구용 `git branch -f` 명령어)을 출력합니다.
8. `-Push` 지정 시 `master`는 일반 푸시, 토픽 및 `hj/main`은 `--force-with-lease`로 푸시합니다.

### 3.3 충돌 해결 절차

`Sync-Upstream.ps1` 실행 중 토픽 rebase 또는 머지 단계에서 충돌이 발생하면 다음과 같이 동작합니다:

1. **rerere 자동 해결**:
   - 이전에 해결했던 동일 충돌 패턴인 경우 `rerere`가 자동으로 해결합니다.
   - 충돌 마커(`<<<<<<<`)가 모두 사라지면 스크립트가 자동으로 `git add -A; git rebase --continue`를 호출하여 계속 진행합니다.
2. **수동 충돌 해결**:
   - 새로운 충돌인 경우 스크립트는 rebase 진행 상태를 유지한 채 충돌 파일 목록을 출력하고 종료 코드 `2`로 중단됩니다.
   - 개발자는 표시된 충돌 파일들을 열어 수동으로 해결합니다.
   - 해결된 파일을 스테이징합니다:
     ```sh
     git add <해결된_파일>
     ```
   - rebase를 계속 진행합니다:
     ```sh
     git rebase --continue
     ```
   - 해당 토픽의 rebase가 끝나면 스크립트를 다시 실행합니다:
     ```pwsh
     pwsh eng/hj/Sync-Upstream.ps1
     ```

### 3.4 업스트림이 같은 영역을 개선했을 때의 판단 기준

업스트림에서 우리가 수정한 영역과 유사한 최적화나 기능을 도입했을 때는 다음 기준에 따라 처리합니다:

1. **우리의 PR이 업스트림에 그대로 채택된 경우**:
   - `git rebase master <topic>` 실행 시 git이 패치 동일성(patch ID)을 감지하여 해당 커밋을 자동으로 건너뜁니다(drop).
   - 토픽 브랜치의 모든 커밋이 채택되어 `master`와 동일해지면 스크립트가 "업스트림에 모두 채택됨" 경고를 출력합니다.
   - 이 경우 `eng/hj/topics.txt`에서 해당 토픽 라인을 삭제하거나 주석 처리합니다.
2. **업스트림이 다른 방식으로 구현/개선한 경우**:
   - 업스트림의 공식 구현 방식을 우선적으로 채택하고, 포크의 커밋을 drop합니다.
   - 대화형 rebase(`git rebase -i`) 대신 명시적 베이스 재지정 명령인 `git rebase --onto`를 사용하여 drop할 커밋을 건너뛰고 나머지 커밋만 새 베이스 위로 재배치합니다:
     ```sh
     # 예: <upstream-commit>이 도입되었으므로, 우리의 이전 커밋 <our-dropped-commit>을 제외하고
     # 그 위에 쌓여 있던 후속 커밋들을 master 위로 재배치
     git rebase --onto master <our-dropped-commit> <topic-branch>
     ```
   - 커밋을 drop한 후에는 반드시 프로세스 예산 통합 테스트(`FormBrowseGitProcessBudgetTests`)를 실행하여 업스트림의 구현만으로도 충분한 성능이 나오는지, 추가적인 포크 패치가 여전히 필요한지 정량적으로 재평가합니다.

---

## 4. 새 기능 추가 시 4대 규칙

업스트림 머지 충돌을 영구적으로 최소화하기 위해 포크에 새로운 기능을 추가할 때는 다음 4가지 규칙을 반드시 준수합니다:

1. **새 기능은 master에서 시작하는 새 토픽 브랜치로 분리한다**:
   - 새 기능이 필요하면 `master`에서 분기하는 새 토픽 브랜치를 만들고, `eng/hj/topics.txt`에 브랜치 이름을 등록합니다.
2. **업스트림 원본 파일의 기존 줄을 삭제하거나 이동하지 않는다**:
   - 기존 코드를 수정하거나 줄 위치를 바꾸면 업스트림에서 해당 메서드를 리팩터링할 때 반드시 충돌이 발생합니다.
   - 업스트림 파일에는 오직 1줄 수준의 최소 등록/호출 훅만 추가합니다.
3. **"항상-반환(Always-Return)" 훅을 작성하지 않는다**:
   - 업스트림 메서드 시작 지점에 무조건 `return`하여 업스트림 원본 로직을 우회/차단하는 침투적인 훅을 작성하지 않습니다.
   - 캐시 히트나 조건부 최적화인 경우에만 조기 반환(`TryGet...`)을 시도하고, 그 외의 경우에는 항상 기본 업스트림 구현으로 fallthrough되도록 작성합니다.
4. **업스트림 클래스 확장 시 partial 클래스 또는 별도 파일을 사용한다**:
   - 비즈니스 로직, 데이터 구조, 캐시 모델 등 본체 구현은 포크 전용 파일(`*.ConfigSnapshot.cs`, `*.MergedBranchesCache.cs` 등)로 철저히 분리하여 원본 파일 diff를 극소화합니다.
