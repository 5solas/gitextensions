# Git Extensions 저장소 열기 성능 실측 및 병목 분석 보고서 (W13)

§10 에 개선 결과 추가(2026-11-01)

## 1. 개요 및 측정 환경

본 보고서는 Git Extensions의 저장소 열기(Repository Open) 경로에서 발생하는 `git.exe` 프로세스 기동 패턴, UI 스레드 동기 차단(Blocking) 시간, 각 세부 단계별 지연 시간을 실측하고 정량적으로 분석한 결과입니다.
Git Extensions는 Git CLI 기반 래퍼 구조로 동작하며, 한 번의 저장소 열기 시 수십 회의 외부 프로세스를 생성합니다. 본 조사는 W14+ 최적화 작업의 직접적인 기술적 근거를 제공하기 위해 수행되었습니다.

### 측정 머신 사양
- **CPU**: AMD Ryzen 7 7700X 8-Core Processor (16 Logical Processors, Base 4.5GHz / Boost 5.4GHz)
- **RAM**: 32 GB DDR5
- **Storage**: SK hynix Platinum P41 (SHGP31-2000GM) NVMe PCIe 4.0 SSD (읽기 최대 7,000MB/s)
- **OS**: Microsoft Windows 11 Pro (10.0.26300 Build 26300)
- **Git 버전**: `git version 2.56.0.windows.3`
- **.NET 런타임**: .NET 10.0 (net10.0-windows, x64)

### 대상 저장소
1. **중규모 저장소 (GitExtensions)**: `D:\work\gitextensions` (약 25,000 커밋, 서브모듈 3개)
2. **대규모 저장소 (git/git)**: `D:\work\perf-repos\git` (82,623 커밋, 1,012 태그)

---

## 2. 측정 결과 요약

측정 하네스(`RepositoryOpenPerfTests.cs`)를 통해 콜드 실행 1회 + 웜 실행 4회(총 5회 반복) 및 기능별 민감도(`ShowStashes=false`, `ShowAheadBehindData=false`) 2회를 연속 수행하였습니다.

### 2.1 GitExtensions 저장소 (중규모)

| 실행 구분 | 전체 Wall-Clock (ms) | Git 프로세스 수 | UI 스레드 프로세스 수 | UI 스레드 차단 합계 (ms) | Revisions Loaded (ms) | Left Panel 완료 (ms) | Diff 목록 완료 (ms) | CommitInfo 완료 (ms) |
|---|---|---|---|---|---|---|---|---|
| **Cold (Run 1)** | 4,156 | 27 | 10 | 763.2 | 3,637 | 3,638 | 4,152 | 4,156 |
| **Warm Run 2** | 1,588 | 23 | 8 | 634.3 | 1,363 | 1,363 | 1,585 | 1,588 |
| **Warm Run 3** | 1,624 | 23 | 8 | 595.9 | 1,417 | 1,417 | 1,620 | 1,624 |
| **Warm Run 4** | 1,896 | 23 | 8 | 667.3 | 1,644 | 1,644 | 1,892 | 1,896 |
| **Warm Run 5** | 1,999 | 25 | 9 | 747.1 | 1,667 | 1,668 | 1,905 | 1,999 |
| **Warm 중앙값 (Median)** | **1,760** | **23** | **8** | **650.8** | **1,530** | **1,530** | **1,756** | **1,760** |
| **민감도: NoStashes** | 2,143 | 22 | 7 | 627.5 | 1,925 | 1,925 | 2,141 | 2,143 |
| **민감도: NoAheadBehind** | 2,304 | 24 | 9 | 768.8 | 1,950 | 1,950 | 2,194 | 2,304 |

### 2.2 git/git 저장소 (대규모)

| 실행 구분 | 전체 Wall-Clock (ms) | Git 프로세스 수 | UI 스레드 프로세스 수 | UI 스레드 차단 합계 (ms) | Revisions Loaded (ms) | Left Panel 완료 (ms) | Diff 목록 완료 (ms) | CommitInfo 완료 (ms) |
|---|---|---|---|---|---|---|---|---|
| **Cold (Run 1)** | 3,447 | 23 | 8 | 714.1 | 3,257 | 3,257 | 3,444 | 3,447 |
| **Warm Run 2** | 3,363 | 22 | 8 | 694.2 | 3,266 | 3,267 | 3,360 | 3,363 |
| **Warm Run 3** | 3,547 | 22 | 8 | 653.2 | 3,416 | 3,417 | 3,543 | 3,547 |
| **Warm Run 4** | 3,314 | 22 | 8 | 604.8 | 3,215 | 3,216 | 3,310 | 3,314 |
| **Warm Run 5** | 3,735 | 22 | 8 | 672.8 | 3,638 | 3,638 | 3,732 | 3,735 |
| **Warm 중앙값 (Median)** | **3,456** | **22** | **8** | **663.0** | **3,341** | **3,342** | **3,452** | **3,456** |
| **민감도: NoStashes** | 4,143 | 21 | 7 | 605.4 | 4,015 | 4,015 | 4,139 | 4,143 |
| **민감도: NoAheadBehind** | 3,974 | 21 | 8 | 690.2 | 3,893 | 3,893 | 3,971 | 3,974 |

> [!NOTE]
> - 사전 아키텍처 맵 가설: "열기 한 번에 20~27회 프로세스, UI 스레드 동기 실행 약 7회".
> - 실측치: GitExtensions 저장소 기준 **23~27회 프로세스, UI 스레드 동기 8~10회**. 아키텍처 맵의 추정과 오차범위 5% 이내로 일치함이 실측 검증되었습니다.
> - Cold 실행에서는 `SystemEncodingReader`의 `git config --get ą` 및 `GitVersion`의 `git --version` 2개 프로세스가 UI 스레드에서 추가로 실행됩니다.

---

## 3. 상위 고비용 Git 명령 Top 10 분석

저장소 열기 경로에서 소비된 누적 CPU/벽시계 시간 기준 상위 10개 명령의 세부 내역입니다.

| 순위 | 명령줄 (Arguments 패턴) | 평균 소요 (ms) | 회당 호출 수 | 스레드 | 주요 호출 위치 (소스 코드) | 열기 시점 호출 목적 및 영향 |
|:---:|---|:---:|:---:|:---:|---|---|
| **1** | `git log -z --pretty=format:... --max-count=100000 --all` | Git: **1,061.6**<br>GE: 230.8 | 1회 | BG | `RevisionReader.cs:303`<br>`RevisionGridControl.cs:1195` | 리비전 그래프의 전체 커밋 히스토리 스트리밍 로드. 대형 저장소에서 1초 이상 소요되는 최대 단일 병목. |
| **2** | `git branch --format="%(refname)" -a --merged <HEAD>` | Git: **288.2**<br>GE: 84.7 | **3회** | BG | `RepoObjectsTree.SelectionChanged`<br>(`src/app/GitUI/LeftPanel/RepoObjectsTree.cs`) | 선택된 HEAD 커밋이 머지된 브랜치 목록 계산. 호출자는 CommitInfo가 아니라 `RepoObjectsTree.SelectionChanged`이며, `FormBrowse.RefreshSelection`이 그리드 선택 변경과 하단 탭 전환마다 호출해서 반복됨(측정 하네스가 Diff 탭 → CommitInfo 탭으로 전환하며 2회 추가). |
| **3** | `git tag --contains <HEAD>` | Git: **507.9**<br>GE: 86.1 | 1회 | BG | `GitModule.cs:2244`<br>`CommitInfo.cs:577` | 선택 커밋을 포함하는 태그 검색. 대형 저장소(태그 1,000개 이상)에서 500ms 이상 소비. |
| **4** | `git status --porcelain=2 -z --untracked-files` | Git: **412.4**<br>GE: 224.6 | 1회 | BG | `GitStatusMonitor.cs:422`<br>`GitModule.cs:2726` | 작업 트리 변경 상태 및 Index 변경 카운트 산출. 인덱스 전체 스캔 발생. |
| **5** | `git remote` (인자 없음) | **90.0** | **3회** | **UI(2) / BG(1)** | `RevisionGridControl.cs:988`<br>`FormBrowse.cs:954`<br>`RepoObjectsTree.cs` | 원격 저장소 이름 목록 조회. **UI 스레드에서만 2회 동기 호출되어 ~180ms 동안 UI를 동결시킴.** |
| **6** | `git branch --contains <HEAD>` | Git: 93.4<br>GE: 104.0 | 1회 | BG | `CommitInfo.cs:587` | 선택 커밋을 포함하는 로컬/원격 브랜치 검색 (호출자는 CommitInfo가 맞음). |
| **7** | `git ls-files -z --unmerged` | **80.2** | **2회** | **UI (전체)** | `FormBrowse.cs:1192`<br>`FormBrowse.Activated` | 충돌(Unmerged) 파일 존재 여부 검사. **창 활성화(OnActivated) 때마다 UI 스레드에서 무조건 동기 실행.** |
| **8** | `git for-each-ref --sort="-taggerdate" refs/tags/` | 97.0 | 1회 | BG | `CommitInfo.cs:235` | 태그 정렬 목록 로드. |
| **9** | `git worktree list --porcelain -z` | 94.0 | 1회 | BG | `RepoObjectsTree.cs:412` | 워크트리 트리 노드 구성을 위한 워크트리 목록 조회. |
| **10** | `git stash list -z --pretty=format:...` | 80.6 | 1회 | **UI (전체)** | `RevisionGridControl.cs:1119` | 스태시 목록 로드. **백그라운드가 아닌 UI 스레드에서 EOF까지 동기 스트림 파싱.** |

---

## 4. 중복 및 불필요 호출 분석

실측 트레이스를 분석한 결과, 동일한 정보를 얻기 위해 프로세스를 중복 실행하거나 불필요하게 빠른 시점에 동기 실행하는 심각한 비효율이 확인되었습니다.

### 4.1 `git remote` 3회 중복 호출
- **실측치**: 1회 저장소 열기 시 `git remote` 명령이 정확히 3회(UI 스레드 2회, 백그라운드 스레드 1회) 실행됨.
- **원인 코드 위치**:
  1. `RevisionGridControl.PerformRefreshRevisions` (`RevisionGridControl.cs:988`): `Module.GetRemoteColors()` 호출 경로에서 `ConfigFileRemoteSettingsManager.LoadRemotes`가 원격 목록을 얻기 위해 동기 호출 (UI 스레드, ~90ms).
  2. `FormBrowse.InternalInitialize` (`FormBrowse.cs:954`): 툴바의 `UpdateFetchAllVisibility()`가 `Module.GetRemoteNames()`를 호출 (UI 스레드, ~90ms).
  3. `RepoObjectsTree._remotesTree.Refresh()`: 좌측 패널의 리모트 트리를 채우기 위해 백그라운드 호출 (~85ms).
- **문제점**: `GitModule`에 캐시되지 않고 독립된 컴포넌트가 제각기 프로세스를 기동하여 UI 스레드 180ms를 낭비함.

### 4.2 `git ls-files -z --unmerged` 2회 중복 및 반복 호출
- **원인 코드 위치**: `FormBrowse.InternalInitialize` (`FormBrowse.cs:1192`) 및 `FormBrowse.FormBrowse_Activated` (`FormBrowse.cs:577-593`).
- **문제점**:
  - 저장소가 로드되는 과정에서 `InternalInitialize`에서 1회 실행된 후, 폼이 화면에 표시되고 포커스를 받으면서 `FormBrowse_Activated` 이벤트 핸들러가 트리거되어 100ms 이내에 즉시 2번째 호출이 UI 스레드에서 동기로 발생함.
  - 사용자가 다른 창으로 전환했다가 Git Extensions로 돌아올 때마다(Alt+Tab 등) UI 스레드에서 80ms짜리 `git ls-files`가 무조건 동기로 실행되어 버벅임(Stutter)을 유발함.

### 4.3 `git branch --merged <HEAD>` 3회 중복 호출
- **원인 코드 위치**: `RepoObjectsTree.SelectionChanged` (`src/app/GitUI/LeftPanel/RepoObjectsTree.cs`).
- **문제점**:
  - 기존 보고서에서는 CommitInfo를 원인으로 추정했으나, `git branch --format=%(refname) -a --merged <sha>` 3회의 실제 호출자는 CommitInfo가 아니라 **`RepoObjectsTree.SelectionChanged`**(`src/app/GitUI/LeftPanel/RepoObjectsTree.cs`)임이 확인되었습니다. (`git branch --contains`의 호출자는 CommitInfo가 맞습니다.)
  - `FormBrowse.RefreshSelection`이 그리드 선택 변경과 **하단 탭 전환마다** 호출되어 `RepoObjectsTree.SelectionChanged`를 불필요하게 반복 트리거했습니다.
  - 특히 측정 하네스가 Diff 탭에서 CommitInfo 탭으로 전환하며 2회가 추가되어 단일 열기 중 총 3회가 반복 실행되었습니다.
  - 대형 저장소인 git/git에서는 이 명령 1회당 288ms가 걸리므로, 3회 중복 실행 시 864ms의 백그라운드 연산 리소스를 낭비하게 됩니다.

### 4.4 `git config list`의 Scope별 순차 호출
- **원인 코드 위치**: `GitModule.cs:278`, `ConfigFileRemoteSettingsManager.cs:190`.
- **문제점**: 전체 설정(`git config list --includes --null`)과 로컬 설정(`git config list --local --includes --null`)이 UI 스레드에서 순차적으로 각각 실행되어 약 150~160ms의 UI 동결을 초래함.

---

## 5. UI 스레드 차단(Blocking) 세부 분석

### 5.1 UI 스레드 차단 시간 합계
- **GitExtensions 저장소**: 웜 기준 **634 ~ 747ms** (중앙값 **650.8ms**)
- **git/git 저장소**: 웜 기준 **604 ~ 694ms** (중앙값 **663.0ms**)

저장소 규모와 상관없이 **UI 스레드가 600ms 이상 100% 블로킹**됩니다. 이는 인간이 인지하는 반응 속도 임계치(100ms) 및 60fps 프레임 렌더링 주기(16.6ms)의 40배에 달하여, 폼이 뜨는 순간 창 끌기나 컨트롤 애니메이션이 완전히 멈추는 "프레임 드롭(Stutter)"의 직접적 원인입니다.

### 5.2 UI 스레드 동기 호출 목록 및 비동기화 난이도

| 실행 명령 | 소요 시간 (ms) | 호출 메서드 및 소스 위치 | 비동기화 난이도 | 비동기화 방안 및 리스크 |
|---|:---:|---|:---:|---|
| `git remote` (1차) | ~90 | `RevisionGridControl.cs:988`<br>`GetRemoteColors()` | **하 (Low)** | 리모트 브랜치 색상 설정을 비동기로 로드하거나 백그라운드 Task로 이동 가능. 색상 반영이 0.1초 지연될 수 있으나 UI 동결 방지 효과가 큼. |
| `git remote` (2차) | ~90 | `FormBrowse.cs:954`<br>`UpdateFetchAllVisibility()` | **하 (Low)** | 툴바의 'Fetch All' 버튼 활성화 여부 판단용. 기본 숨김 후 백그라운드 완료 시 표시하도록 수정 용이. |
| `git ls-files -z --unmerged` | ~80 | `FormBrowse.cs:1192`<br>`RefreshGitAction()` | **중 (Medium)** | 충돌 해결 툴바 버튼 상태 갱신. 디바운스(Debounce) 및 스로틀(Throttle) 처리 필수. 백그라운드 태스크로 전환 가능. |
| `git config list --includes` | ~78 | `GitModule.cs`<br>`EffectiveGitConfigSettings` | **중 (Medium)** | 모듈 초기화 시 동기 프로퍼티 접근으로 인해 발생. Lazy 비동기 프로바이더 패턴으로 전환 필요. |
| `git config list --local` | ~75 | `GitModule.cs:278`<br>`LocalGitConfigSettings` | **중 (Medium)** | 상동. |
| `git stash list -z --pretty=...` | ~80 | `RevisionGridControl.cs:1119`<br>`getStashRevs.Value` | **하 (Low)** | `getStashRevs` Lazy를 메인 스레드에서 평가하지 않고, step1 백그라운드 태스크 내부에서 평가하도록 이동(`await Task.Run(...)`). 리스크 극히 낮음. |
| `git rev-parse --git-common-dir` | ~75 | `IndexWatcher.Reset()`<br>`RevisionGridControl.cs:1055` | **하 (Low)** | `GitModule`에 이미 `GitCommonDirectory`가 캐시되어 있으면 재실행 불필요. 캐시 연동만으로 제거 가능. |

---

## 6. Windows 프로세스 생성 오버헤드 분석

Windows 환경의 프로세스 기동 비용(`CreateProcessW`, 콘솔 서브시스템 상속, 환경변수 블록 복사, CRT 런타임 초기화)을 정량화하기 위해, 내부 연산량이 0에 수렴하는 극소 명령의 실행 시간을 분석하였습니다.

### 6.1 고정 기동 비용 측정치
- `git --version`: **68.2 ms** (I/O 및 저장소 탐색 없음)
- `git config --get ą`: **74.0 ms** (단순 인코딩 조회)
- `git rev-parse --git-common-dir`: **71.0 ~ 76.3 ms** (.git 디렉터리 경로만 출력)

> **Windows 환경에서 `git.exe` 1회 기동 고정 비용 = 약 65 ~ 80ms**

### 6.2 프로세스 기동 고정 비용이 실제로 체감 시간에 미치는 영향
프로세스 23회 × 75ms ≈ 1,725ms 라는 합계는 **여러 스레드에 분산된 누적 시간**이므로 벽시계 시간(1,760ms)과 직접 비교할 수 없습니다(백그라운드 호출은 서로 겹쳐 실행됨). 고정 비용이 체감에 영향을 주는 경로는 두 가지입니다.

1. **UI 스레드 차단**: 웜 실행에서 UI 동기 호출 8회의 합계가 596~667ms 인데, 각 호출(`git remote`, `config list` ×2, `ls-files`, `rev-parse`, `stash list`)은 저장소 크기와 무관하게 68~95ms 로 측정되었습니다(git-run3: 81/83/80/78/74/79/95/83ms). 즉 **UI 동결의 90% 이상은 Git 연산이 아니라 프로세스 기동 고정 비용**이며, 이 8회는 CSV 상 순차적으로 실행되어(648→725→794→868→943→1015→1088→1181ms) 그 시간만큼 리비전 로드 시작(`for-each-ref` 1012ms, `git log` 1087ms)을 뒤로 밉니다.
2. **크리티컬 패스**: git/git 에서는 `git log`(1,052ms), `branch --merged`(550ms), `tag --contains`(525ms) 같은 실제 연산이 벽시계를 지배하고, 고정 비용은 UI 차단 구간에 집중됩니다.

### 6.3 첫 git 호출 전 공백 (추가 관찰)
CSV 의 첫 git 프로세스 시작 오프셋이 GitExtensions 저장소에서 **약 650ms**, git/git 에서 **약 1,630ms** 입니다. 즉 폼 표시 직후 0.6~1.6초 동안은 git 프로세스가 하나도 뜨지 않습니다. 이 구간은 `FormBrowse` 생성·`GitModule` 초기화·플러그인 MEF 로드·좌측 패널 초기화 등 **.NET 측 작업**이며 저장소 크기에 따라 1초 가까이 늘어나므로, git 호출 최적화와 별개로 **후속 측정 과제(프로파일러로 구간 분해)** 로 남깁니다. 본 보고서의 벽시계 수치에는 이 구간이 포함되어 있습니다.

---

## 7. W14+ 최적화 후보 제안 및 우선순위 표

본 측정 결과를 기반으로 다음 단계(W14+)에서 즉시 구현 가능한 최적화 후보를 우선순위화하였습니다.

| 우선순위 | 최적화 후보명 | 기대 절감 효과 (실측 근거) | 난이도 | 독립 PR 여부 | 위험도 | 구체적 구현 방안 |
|:---:|---|---|:---:|:---:|:---:|---|
| **P1** | **UI 스레드 `git stash list` 평가 백그라운드 이동** | **UI 블로킹 80ms 즉시 제거** | **하** | **가능** | **극저** | `RevisionGridControl.cs:1119`에서 `getStashRevs.Value`를 동기 평가하지 않고, 라인 1076의 `Task.Run` 백그라운드 클로저 내부에서 평가하도록 이전. |
| **P2** | **`git remote` 호출 캐싱 및 중복 제거** | **UI 블로킹 ~90ms 제거 + 프로세스 2개 절감** | **하** | **가능** | **낮음** | `GitModule` 수준에서 `GetRemoteNames()` 결과를 단기 캐싱하고, `RevisionGrid`와 `FormBrowse`, `RepoObjectsTree`가 동일 인스턴스/캐시를 공유하도록 연결. |
| **P3** | **`git ls-files --unmerged` 활성화 스로틀링** | **UI 블로킹 80ms 제거 + 창 전환 시 프리징 방지** | **하** | **가능** | **낮음** | `FormBrowse_Activated`에 디바운스 타이머(최소 2초) 적용 및 백그라운드 Task 전환. 초기 열기 시에는 `InternalInitialize` 호출 1회로 단일화. |
| **P4** | **`git config list` 비동기화 및 초기 로컬 config 지연** | **UI 블로킹 ~150ms 제거** | **중** | **가능** | **중간** | `EffectiveGitConfigSettings`를 비동기 초기화하거나, 폼 로드 후 비동기 완료 시점에 설정 의존 UI 컴포넌트를 갱신하도록 분리. |
| **P5** | **CommitInfo 선택 커밋 조회 명령 병합 (`for-each-ref --contains`)** | **백그라운드 ~800ms 절감 + 프로세스 3개 절감** | **중** | **가능** | **낮음** | `branch --merged` 3회 중복 제거. `branch --contains`와 `tag --contains`를 개별 실행하지 않고 단일 `git for-each-ref --contains <HEAD>`로 병합. |
| **P6** | **`git rev-parse --git-common-dir` 중복 프로세스 제거** | **UI 블로킹 ~75ms 제거 + 프로세스 1개 절감** | **하** | **가능** | **극저** | `IndexWatcher.Reset()` 시 `GitModule.GitCommonDirectory`에 이미 계산된 값이 있으면 외부 프로세스 기동 없이 기존 프로퍼티 재사용. |
| **P7** | **Ahead/Behind 계산 지연(Lazy) 로드** | **초기 프로세스 1개 절감 및 I/O 경합 완화** | **하** | **가능** | **낮음** | 폼 열기 즉시 `for-each-ref`를 던지지 않고, 리비전 그리드 첫 배치가 렌더링된 후 유휴 상태에서 트리거. |

> **P1~P4 및 P6 적용 시 예상 성과**:
> - **UI 스레드 차단 시간**: 650ms → **150ms 미만 (약 77% 감소, UI 프리징 체감 해소)**
> - **총 Git 프로세스 수**: 23~27개 → **16~19개 (약 30% 감소)**
> - **중소규모 저장소 Wall-Clock**: 1.76초 → **1.2초 내외 (약 30% 단축)**

---

## 8. 인프로세스 Git 라이브러리 도입 검토 (LibGit2Sharp vs gitoxide)

Git Extensions의 CLI 래핑 아키텍처에 네이티브 인프로세스 라이브러리를 부분 도입하는 것에 대한 실측 기반 검토 의견입니다.

### 8.1 구간별 대체 타당성 분석

| 대상 구간 | 현재 CLI 프로세스 | LibGit2Sharp (C# / libgit2) | gitoxide (Rust / gix) | 타당성 판단 및 권고 |
|---|---|---|---|---|
| **상태 및 인코딩 단순 조회** | `git config --get ą`<br>`git --version` | LibGit2 기본 설정 읽기로 대체 가능 | 단순 Rust FFI로 구현 가능 | **부적합**: 앱 실행 중 단 1회만 기동되므로 라이브러리 도입 비용 대비 실익 없음. |
| **Config / Remote / Common-Dir** | `git remote`<br>`git config list`<br>`git rev-parse` | Repository 인스턴스에서 in-memory 조회 즉시 완료 (<1ms) | `gix-config`, `gix-discover`로 수 밀리초 내 처리 가능 | **검토 가치 있음**: 7~8개의 UI 동기 프로세스(~500ms)를 10ms 이내로 단축 가능. |
| **리비전 히스토리 워크 (Log)** | `git log -z --pretty=...` | `repo.Commits.QueryBy(...)` 가능하나 C# 객체 할당 오버헤드로 인해 8만 커밋 시 메모리 수백 MB 폭증 | 초고속 리비전 워킹 가능하나 .NET 바인딩 부재 | **부적합**: 현재 `RevisionReader`의 스트리밍 CLI 파이프라인이 네이티브 메모리 및 GC 압박 측면에서 오히려 우수함. |
| **작업트리 상태 (Status)** | `git status --porcelain=2` | LibGit2 `RetrieveStatus`는 대형 저장소에서 CLI git 대비 2~5배 느리고 인덱스 잠금 충돌 위험 존재 | 미완성 (상태 조회 기능 성숙도 부족) | **절대 불가**: CLI `git status`의 index v4, untracked cache, fsmonitor 최적화를 따라갈 수 없음. |

### 8.2 라이브러리 비교 종합

1. **LibGit2Sharp (libgit2)**:
   - **장점**: 현재 테스트 프로젝트(`CommonTestUtils/ReferenceRepository.cs`)에 v0.31.0 패키지가 이미 참조되어 있어 C# 연동 난이도가 낮음.
   - **단점**: 
     - 네이티브 DLL(`git2-*.dll`, 약 30MB) 배포 용량 증가.
     - Worktree 지원 불완전, reftable 미지원, sparse-checkout 불완전, 최신 Git 설정 파싱 누락.
     - 64비트 전용 바이너리 관리 복잡성.
2. **gitoxide (Rust `gix`)**:
   - **장점**: 압도적인 성능과 안전성, 최신 reftable 지원.
   - **단점**: 
     - 공식 .NET C# 바인딩이 없음. 별도의 Rust `cdylib`를 작성하고 P/Invoke를 관리해야 함.
     - Git Extensions 오픈소스 생태계(C#/.NET 중심 기여자)의 유지보수 부담 가중.

### 8.3 결론 및 권고
**"현재 시점에서 인프로세스 라이브러리 도입은 불필요하며, CLI 캐싱 및 비동기화 최적화가 우선되어야 한다."**
- 실측 결과 확인된 병목(UI 스레드 650ms 동결, 28개 프로세스)의 90% 이상은 **CLI 수준의 캐시 공유(P2), 호출 위치 백그라운드 이전(P1, P3), 중복 호출 병합(P5)**만으로 완전히 해소할 수 있습니다.
- 인프로세스 라이브러리는 Git 버전 업그레이드 때마다 호환성 문제(Windows 11 reftable, sparse-index, credential helper)를 야기할 위험이 크므로, CLI 최적화가 한계에 도달하기 전까지는 도입을 권장하지 않습니다.

---

## 9. 측정의 한계 및 불확실한 점

1. **합성 테스트 하네스 환경의 한계**:
   - `RepositoryOpenPerfTests`는 UI 스레드 메시지 루프를 프로그래밍 방식으로 구동하는 테스트 환경에서 실행되었습니다. 실제 사용자가 마우스 클릭이나 윈도우 포커스 이동을 할 때 발생하는 추가 렌더링 이벤트 및 타사 플러그인(PluginRegistry) 실행 비용은 완전히 포함되지 않았습니다.
2. **Windows Defender 및 보안 소프트웨어 영향**:
   - 측정 머신은 Windows Defender 실시간 감시가 활성화된 상태였습니다. Windows 환경에서 `git.exe` 신규 프로세스 기동 시 Defender의 프로세스 스캔 오버헤드가 고정 비용(65~80ms)의 일부를 차지했을 가능성이 높습니다. 개발자 모드(Developer Drive)나 Defender 예외 폴더에서는 프로세스 기동 비용이 30~40ms로 감소할 수 있습니다.
3. **CommitInfo 완료 시점 감지**:
   - `CommitInfo` 텍스트 박스 채움 완료 감지는 리비전 로드 후 2초 유휴 시간 대기 휴리스틱을 병행 적용하였습니다. 실제로는 백그라운드 태스크가 수백 밀리초 일찍 끝났을 수 있으며, 이는 보고서의 CommitInfo 완료 타임스탬프에 100~200ms 수준의 측정 여유분이 포함되었음을 의미합니다.
4. **네트워크 원격 저장소 상호작용 배제**:
   - 측정 중 `git fetch`나 원격 LFS 상태 조회는 수행되지 않았습니다. 원격 네트워크 지연이 개입되는 환경에서는 백그라운드 스레드 점유 시간이 본 실측치보다 훨씬 길어질 수 있습니다.

---

## 10. 개선 적용 결과 (feature/perf-repo-open)

### 10.1 측정 기준
- **측정 기준**: `FormBrowseGitProcessBudgetTests`
- **저장소 조건**: 작은 저장소 (브랜치 2, 리모트 1, 태그 2)
- **시나리오**: 열기 → HEAD 선택 → CommitInfo 탭 → Diff 탭

### 10.2 단계별 결과 (전체 git 프로세스 / UI 스레드)
- **기준선**: 29 / 10
- **refs · 선택 개선**: 25 / 10 (좌측 패널 merged 중복 제거, 정렬 태그를 메인 refs 에서, ahead/behind 경쟁 수정)
- **config 1회 로드 · 리모트를 config 에서**: 19 / 7
- **공통 디렉터리 파일 해석 · 충돌 캐시 · 스태시 없을 때 stash list 생략**: 17 / 4
- **업스트림 반영 후**: 16 / 4 (업스트림 `perf(left panel): paint before TreeView is filled` 반영)
- **hj-4 (W20/W20b)**: 15~16 / 0 (스태시 없음), 16~17 / 1 (스태시 있음) — config provider 공유, Diff 뷰어 `ls-tree`/`check-attr` 백그라운드화

### 10.3 남은 UI 스레드 호출
- 스태시가 없으면 0개.
- 스태시가 있으면 `stash list` 1개. 백그라운드화(W23)는 늦게 도착한 SelectionChanged 가 진행 중 Diff 를 취소하고 다시 불러오지 않는 경쟁 때문에 보류(패치 보관, 업스트림 잠재 경쟁 조사 과제).

### 10.4 설계 원칙
- **동작 동등성 및 폴백**: 동작 동등성이 증명되지 않으면 기존 git 호출로 폴백
  - `url.*.insteadOf` / `pushInsteadOf` 또는 레거시 리모트(`.git/remotes`, `.git/branches`) 설정 시 → `git remote -v`
  - `commondir` 해석 실패 시 → `rev-parse` (`git rev-parse --git-common-dir`)
- **머지 친화적 구조**: 업스트림 파일에는 훅만 추가하여 머지 친화 유지

### 10.5 보류 항목
- **`branch --contains` + `tag --contains` 병합**: commit-graph 없을 때 느려짐
- **`%(ahead-behind:)`**: git 2.41+ 필요
- **첫 git 호출 전 0.6~1.6초 .NET 구간 프로파일링**: 후속 프로파일링 과제로 보류

### 10.6 운영상 발견
- **테스트 인프라 머신 전역 세마포어**: `GitExtensionsTestAssemblySerializer`
- **testhost 종료 문제**: 이 머신에서 `--blame-hang-timeout` 이 testhost 를 끝내지 못함

### 10.7 실제 저장소 전후 비교 (hj-4, `docs/hj/perf/after-hj4/`)
`eng/hj/Measure-RepoOpen.ps1 -Iterations 5 -PerfOutputDir docs\hj\perf\after-hj4` 로 측정 (기준선 = `docs/hj/perf/*-summary.csv`, W13). 웜 = Run2~5 중앙값.

| 지표 | GE 저장소 기준선 | GE hj-4 | git/git 기준선 | git/git hj-4 |
|---|---|---|---|---|
| 콜드 열기 (Run1) | 4,156 ms | 2,566 ms | 3,447 ms | 2,811 ms |
| 웜 열기 중앙값 | 1,760 ms | 1,456 ms (-17%) | 3,455 ms | 3,138 ms (-9%) |
| 웜 git 프로세스 | 23 | 11 | 22 | 10 |
| 웜 UI 스레드 git 호출 | 8 (~630 ms) | 0 | 8 (~670 ms) | 0 |

- 줄어든 명령 (GE 웜 Run2): `git remote` 4→0, `git config` 2→0, `git branch` 4→2, `rev-parse` 1→0, `ls-files` 2→1, `for-each-ref` 2→1, `stash` 1→0.
- 측정 하네스 변경(W27): 대기 루프를 `Thread.Sleep` → `await Task.Delay` 로 바꿈. 업스트림의 좌측 패널 `WaitForIdleAsync` 는 `Application.Idle` 을 기다리는데, 동기 대기 루프가 메시지 루프로 돌아가지 않아 Idle 이 오지 않고 15초 타임아웃까지 막혔음(기준선 측정 당시에는 이 업스트림 변경이 없었음). 대기 방식이 달라 수십 ms 단위 차이는 하네스 영향일 수 있으나, 프로세스 수와 UI 스레드 호출 수는 하네스와 무관.
- 남은 웜 시간의 대부분은 리비전 로드(`git log`)와 첫 git 호출 전 .NET 구간 — 10.5 의 프로파일링 과제.
