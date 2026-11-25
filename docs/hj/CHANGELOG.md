# 변경 이력 (hj 포크)

Git Extensions 포크(`5solas/gitextensions`)의 릴리스별 변경 내용이다. 최신 버전이 위에 있다.
설치: `scoop update` 후 `scoop update gitextensions-hj` (버킷 `5solas/scoop-hj`).

git 프로세스 수는 예산 테스트(`FormBrowseGitProcessBudgetTests`)의 시나리오 기준이다: 작은 테스트 저장소를 열고 HEAD 를 선택해 파일 하나를 볼 때까지 실행되는 git 프로세스 수.

---

## 7.3.0-hj-10 (2026-11-26)

upstream 4개 커밋을 반영했다(`063b76ce3` 까지). 포크 쪽 기능 변경은 없다.

### 반영한 upstream 커밋

- `f00065009` feat(DeleteUnusedBranches): offer agent session refs for deletion (#13420)
- `b1fba546b` fix(blame): save and restore the blame splitter position (#13480)
- `6b4f8bd55` feat(FileViewer): make the max diff file size configurable (#13481)
- `063b76ce3` fix(submodules): show sha instead of an exception

### 충돌과 해결

- 자동 반영(`hj-upstream-autosync`)이 `feature/perf-repo-open` 리베이스에서 멈췄다. 충돌 파일은 `tests/app/UnitTests/GitUI.Tests/Editor/FileViewerTests.cs` 하나다.
- 원인: upstream `6b4f8bd55` 가 그 테스트 파일 끝에 테스트 4개를 추가했고, 포크 커밋(파일 diff 표시 중 git 을 UI 스레드에서 실행하지 않음)도 같은 자리에 테스트 1개를 추가했다.
- 해결: 양쪽 테스트를 모두 남겼다(using 은 포크 쪽 목록이 upstream 것을 포함). 제품 코드의 충돌은 없었다.
- 나머지 토픽(`feature/ghostty-console`, `feature/build-tools-support`, `hj/infra`)은 충돌 없이 리베이스됐다. 리베이스 전후 커밋 비교(`git range-diff`)에서 달라진 것은 위 커밋 하나뿐이다.

### 포크 패치와 유사한 upstream 변경

- 자동 대조(키워드·경로) 결과 없음.

---

## 7.3.0-hj-9 (2026-11-26)

upstream 기준은 hj-8 과 같다(`bd69886b7`). hj-8 에서 "다음 릴리스 예정"으로 남긴 항목이 들어갔다.

### 수치 요약

| 항목 | hj-8 | hj-9 |
| :--- | ---: | ---: |
| 같은 저장소에서 다음 파일을 볼 때 git 프로세스 | 1 | 0 |
| 파일 표시 시간 중앙값 (gitextensions 저장소, 64개 파일) | 58 ms | 18 ms |
| 저장소 열기 git 프로세스 (스태시 없음 / 있음) | 8 / 9 | 8 / 9 |
| 다른 저장소에서 되돌아오는 전환의 git 프로세스 | 4 | 4 |

측정은 개발 PC(로컬 NTFS, git 2.56.1) 기준이다.

### 변경 내용

**1. `git check-attr` 도 상주 프로세스로** (커밋 `9836a9433`, `73d0a26f7`)
- 파일을 볼 때 남아 있던 `git check-attr` 1회를 저장소당 하나의 상주 `git check-attr --stdin -z` 프로세스 요청으로 바꿨다.
- git 은 `.gitattributes` 를 프로세스가 사는 동안 다시 읽지 않는다(실험으로 확인). 그래서 요청마다 git 이 읽을 파일들(요청 경로의 모든 상위 폴더의 `.gitattributes`, `info/attributes`, index, 전역·시스템 attributes)의 크기와 수정·생성 시각을 비교하고, 다르면 프로세스를 다시 띄운다. 최근 2초 안에 바뀐 파일이 있으면 예전처럼 단발 실행한다.
- 이진/텍스트 판정 규칙은 복제하지 않았다(upstream 파서를 그대로 탄다).
- 3초 넘게 쉬었다가 다음 파일을 보면 프로세스 1개가 다시 시작된다.

**2. fsmonitor 진단과 유휴 데몬 종료** (커밋 `a9dcfa99e`, `5e34a0672`)
- Git 설정 고급 페이지의 `core.fsmonitor` 옆에 권장 / 비권장 / 지원 안 됨을 표시한다(로컬 고정 드라이브, index 1 MB 이상, 서브모듈 10개 미만이면 권장). git 을 실행하지 않는다.
- 이번 실행에서 연 저장소 중 최근에 쓴 5개만 fsmonitor 데몬을 유지하고, 나머지는 백그라운드에서 `git fsmonitor--daemon stop` 으로 끝낸다. 창에 열려 있는 저장소와 `core.fsmonitor` 가 켜진 것으로 확인되지 않은 저장소는 건드리지 않는다. 데몬이 떠 있는지는 이름 있는 파이프로 판단한다.
- 데몬 1개는 메모리 약 8.7 MB, 스레드 14개, 핸들 112개를 쓴다(실험).

**3. 설정 화면** (커밋 `5e34a0672`)
- 설정 > Git Extensions > Advanced > **Performance** 페이지가 생겼다. hj-7 ~ hj-9 의 설정 8개를 여기서 바꾼다. 각 항목 아래에 설명이 있다.

| 화면 항목 | 설정 키 | 기본값 |
| :--- | :--- | :--- |
| Start the git binary directly instead of its launcher | `UseDirectGitBinary` | 켬 |
| Read file contents with a persistent git process | `UseGitBroker` | 켬 |
| Read git attributes with a persistent git process, too | `UseGitBrokerForAttributes` | 켬 |
| Limit for the config and the stashes of recent repositories | `RepoSnapshotMemoryLimit` | 1024 MB |
| Limit for file contents read by the persistent git process | `GitBrokerMemoryLimit` | 512 MB |
| Allow the status update to refresh the index if it was slow | `RefreshIndexAfterSlowStatus` | 켬 |
| Stop the daemons of repositories which have not been used recently | `StopIdleFsMonitorDaemons` | 켬 |
| Number of daemons to keep | `MaxFsMonitorDaemons` | 5 |

**4. hj-8 리뷰에서 남았던 지적 수정**
- R7: 충돌 확인 플래그를 잠금 안에서 기록, 컨트롤이 사라지면 대기 취소, 취소된 워크트리 로드가 늦게 상태를 바꾸지 않음.
- R8: 파일 지문이 없는 설정 기록으로는 상주 프로세스 사용 여부를 기억하지 않음, 두 상주 프로세스 모두 `-c core.fsmonitor=false` 로 실행하고 `:` 로 시작하는 이름은 보내지 않음, 첫 요청이 취소돼도 시작 중인 프로세스를 3초까지 기다림.
- hj-8 릴리스 때 원인 미확인이던 단위 테스트 간헐 실패(`Config_should_be_read_again_if_global_config_changed`)는 **테스트 격리 문제**로 확인됐다. 테스트 도우미 저장소의 지연된 설정 재읽기가 전역 명령 기록에 섞인 것이며(지연을 넣어 재현), 제품 결함이 아니다. 테스트가 자기 저장소의 명령만 세도록 고쳤다.

### 알려진 한계

- **상주 `check-attr` 프로세스는 저장소 작업 폴더를 현재 폴더로 잡는다.** 마지막으로 파일을 본 뒤 약 3초 동안 그 저장소(또는 워크트리)의 루트 폴더를 옮기거나 지우면 실패할 수 있다. 3초 뒤 프로세스가 끝난다.
- 크기와 수정·생성 시각을 모두 그대로 둔 채 내용만 바꾼 attributes 파일은 감지하지 못한다.
- `HOME` 이 완전한 Windows 경로가 아니면(예: `/c/Users/...`) 상주 `check-attr` 를 쓰지 않는다.
- 저장소 스냅샷을 끄면(`RepoSnapshotMemoryLimit` = 0) 상주 프로세스도 쓰지 않는다(설정이 최신인지 확인할 수단이 없어지기 때문).
- 스테이징 등으로 index 가 바뀌면 최대 2초 동안 `check-attr` 가 단발 실행으로 돌아간다.
- **fsmonitor "권장" 기준(index 1 MB 이상)은 검증되지 않았다.** 실험에서 파일 2만 개(index 1.6 MB) 저장소는 fsmonitor 를 켜도 `git status` 가 빨라지지 않았다(83~103 ms 대 90~106 ms). 큰 저장소에서 실제 효과를 본 뒤 기준을 조정해야 한다.
- 데몬을 끈 뒤 다른 도구(IDE, 터미널)가 그 저장소에서 처음 실행하는 `git status` 가 한 번 약 40 ms 느려진다(파일 2만 개 기준, 저장소가 클수록 커진다).
- junction, subst, 8.3 경로로 연 저장소의 데몬은 감지하지 못해 끄지 않는다. 파이프 이름 규칙은 git 내부 구현이라 바뀌면 아무것도 끄지 않게 된다.
- 앱을 끝낼 때는 데몬을 정리하지 않는다.
- 설정 페이지의 문구는 영문만 있다. 다른 DPI 와 어두운 테마에서의 화면은 확인하지 않았다(자동 테스트는 96 dpi 배치만).
- hj-8 의 알려진 한계(index 갱신 중 다른 창의 git 명령 실패 가능, 표식 없는 충돌의 배너 지연 등)는 그대로다.

### 코드 리뷰에서 나온 결함과 처리

| 리뷰 | 대상 | 결과 | 처리 |
| :--- | :--- | :--- | :--- |
| R9 | 상주 `check-attr` | 차단 결함 없음. 중간 1, 낮음 5 | 3건 수정(`73d0a26f7`): 전역 attributes 파일을 git 과 다른 곳에서 찾아 낡은 속성이 남을 수 있던 문제 등. 오류 출력 상한은 upstream 의 프로세스 시작 코드를 복제해야 해서 넣지 않음. 2건 기각 |
| R10 | fsmonitor | 차단 결함 없음. 중요 1, 경미 4 | 2건 수정(`5e34a0672`): 프로그램을 시작할 때 연 저장소가 데몬 추적에 등록되지 않던 문제, 진단의 UI 스레드 파일 확인. 설정 화면 없음은 Performance 페이지로 해결. 2건 기각 |
| R11 | 설정 페이지, 마지막 수정 | 차단 결함 없음. 경미 3 (스킬 대신 수동 리뷰) | 수정하지 않음: `XDG_CONFIG_HOME` 이 있어도 `HOME` 형식이 맞지 않으면 상주 `check-attr` 를 포기함, 필요 없는 `APPDATA` 경로 감시, 환경 변수 설정의 반복 호출. 모두 느려지는 쪽이고 결과는 옳다 |

### 다음 후보

- 보류: 3번 2단계(ref 가 같으면 전환 시 `git log` 생략). gitextensions 크기 저장소에서 전환 373 ms → 170~270 ms 로 추정되나, upstream 과 공유하는 파일에 훅이 필요하다.
- 보류: 6-A(`for-each-ref` 2회 병합), 9번(gitoxide 읽기 전용 가속 실험).

---

## 7.3.0-hj-8 (2026-11-25)

upstream 기준은 hj-7 과 같다(`bd69886b7`). 성능 개선 로드맵(W30)의 3번 1단계, 4번 1단계, 5번, 6-B, 6-C 가 들어갔다.

### 수치 요약

| 항목 | hj-7 | hj-8 |
| :--- | ---: | ---: |
| 저장소 열기 git 프로세스 (스태시 없음 / 있음) | 11 / 12 | 8 / 9 |
| 다른 저장소에서 되돌아오는 전환의 git 프로세스 | 6~8 | 4 |
| 전환 중 UI 스레드에서 실행되는 git | 0~2 | 0 |
| 같은 저장소에서 다음 파일을 볼 때 git 프로세스 | 3 | 1 |
| 파일 표시 시간 중앙값 (gitextensions 저장소, 48개 파일) | 141 ms | 58 ms |
| 전환 완료 시간 중앙값, 스태시 3개인 작은 저장소 | 203 ms | 142 ms |
| 전환 완료 시간 중앙값, gitextensions (1.8만 커밋, 스태시 없음) | 375 ms | 373 ms |

측정은 개발 PC(로컬 NTFS, git 2.56.1) 기준이다. 회사 환경(백신, 네트워크 드라이브, 큰 저장소)에서는 재지 않았다.

### 변경 내용

**1. 저장소로 되돌아올 때 설정과 스태시 목록을 다시 쓴다** (커밋 `887fe52ed`, `f704b594d`)
- 최근에 연 저장소의 `git config list` 결과와 `git stash list` 결과를 메모리에 둔다(최대 200개 저장소).
- 다시 쓰기 전에 git 이 읽을 파일의 내용(SHA-256)과 환경 변수를 비교한다. 다르거나 판단할 수 없으면(WSL, reftable, `GIT_DIR` 설정 등) git 을 다시 실행한다.
- 함께 고쳐진 기존 문제: 전환 직후 밖에서 바뀐 설정을 못 보고 옛 값을 쓰는 경우가 있었다.
- 스태시가 없는 큰 저장소는 전환 시간이 거의 줄지 않는다. 남은 시간의 대부분은 `git log`(약 240 ms)와 `git status`(약 160 ms)다.

**2. 파일 내용을 상주 git 프로세스로 읽는다** (커밋 `8daf6e028`, `c0cc661ef`)
- 파일을 볼 때마다 실행하던 `git ls-tree`, `git cat-file blob` 을 저장소당 하나의 상주 `git cat-file --batch-command` 프로세스 요청으로 바꿨다. `git check-attr` 1회는 남아 있다.
- 상주 프로세스는 최대 8개이고, 15초 동안 요청이 없으면 끝난다. 앱이 git 명령 대화상자(fetch, pull, gc 등)를 실행하기 전에도 모두 끝낸다. 메모리는 프로세스당 약 3 MB.
- 읽은 파일 내용은 메모리에 둔다(상한 512 MB, 파일당 16 MB).
- 상주 프로세스가 처리하지 못하는 경우(폴더, 없는 객체, WSL, reftable, partial clone, 오류, git 2.45 미만)는 예전처럼 단발 실행한다.
- 파일을 빠르게 넘겨 요청이 취소돼도 프로세스를 다시 띄우지 않는다(취소 20회: 프로세스 시작 21회 → 1회).

**3. 저장소를 열 때 `git worktree list` 를 실행하지 않는다** (커밋 `677430dc0`)
- 추가 워크트리가 없는 저장소는 `.git` 의 파일에서 워크트리 노드를 만든다. 노드를 펼치면 `git worktree list` 를 실행한다.
- 추가 워크트리가 있거나 판단할 수 없으면(서브모듈, bare, reftable 등) 예전처럼 바로 실행한다.

**4. 저장소를 열 때 `git ls-files --unmerged` 를 실행하지 않는다** (커밋 `677430dc0`)
- 충돌 확인은 상태 모니터의 `git status` 결과를 기다려 쓴다. 병합·리베이스·패치 중이거나 모니터가 멈춰 있으면 예전처럼 바로 실행한다.
- 함께 고쳐진 기존 문제: 조회 도중 index 가 바뀌었는데도 그 결과를 현재 index 의 것으로 기억하는 경우가 있었다.

**5. 느려진 상태 조회를 스스로 복구한다** (커밋 `677430dc0`)
- 파일의 수정 시각만 대량으로 바뀌면(빌드, 브랜치 전환 등) 백그라운드 `git status` 가 매번 파일 내용을 다시 비교해 느려진다(실험: 28 ms → 140 ms 가 계속됨).
- 조회가 150 ms 이상 걸렸고, 다른 git 프로세스와 `index.lock` 이 없으면 다음 조회 1회에서 index 갱신을 허용한다(5분에 최대 1회). 실험에서는 갱신 1회 뒤 28 ms 로 돌아왔다.

### 새 설정 키

설정 화면에는 아직 없다. 설정 파일(`%APPDATA%\GitExtensions\GitExtensions\GitExtensions.settings`)에서 바꾼다.

| 키 | 기본값 | 의미 |
| :--- | :--- | :--- |
| `RepoSnapshotMemoryLimit` | 1024 | 저장소별 설정·스태시 재사용의 메모리 상한(MB). 0 이면 끔 |
| `UseGitBroker` | true | 상주 `cat-file` 프로세스 사용 |
| `GitBrokerMemoryLimit` | 512 | 파일 내용 캐시 상한(MB). 0 이면 캐시 끔 |
| `RefreshIndexAfterSlowStatus` | true | 느린 상태 조회 뒤 index 갱신 허용 |

### 알려진 한계

- **index 갱신과 다른 창의 git 명령**: 갱신을 허용한 `git status` 가 잠금을 잡고 있는 동안(실험에서 약 16 ms, 큰 저장소에서는 더 길다) 다른 창에서 `git add`, 커밋 등을 실행하면 그 명령이 `index.lock` 오류로 실패할 수 있다. 5분에 최대 1회이고, `RefreshIndexAfterSlowStatus=false` 로 끌 수 있다.
- **표식 파일이 없는 충돌의 배너**(`stash pop`, `checkout -m` 등): 상태 조회가 끝날 때까지 늦게 표시된다. 보통 1초 안팎, 최대 30초.
- **검증할 수 없는 저장소**(WSL 등)는 설정을 읽은 지 10초가 지나면 새 모듈이 만들어질 때 `git config list` 를 다시 실행한다. 전보다 git 호출이 늘 수 있다.
- **재검증용 파일 읽기**(전환당 약 3~5 ms)가 UI 스레드에서 실행된다. 네트워크 드라이브에서는 더 걸릴 수 있다.
- **상주 프로세스는 git 2.45 이상**에서만 쓴다(아래 보안 항목).
- WSL, reftable, 네트워크 저장소, 다른 계정이 소유한 저장소, git 2.56.1 이외의 버전은 "재사용하지 않음/단발 실행" 판정을 코드와 단위 테스트로만 확인했고 실제 환경에서 시험하지 않았다.
- 탭 전환 직후 75 ms 안에 파일을 고르면 선택이 사라질 수 있다. upstream 의 기존 동작이며 고치지 않았다.

### 코드 리뷰에서 나온 결함과 처리

항목마다 구현자와 다른 워커가 코드 리뷰를 했다(원문은 `artifacts/review/R4~R8.md`, git 에 올리지 않는 폴더).

| 리뷰 | 대상 | 결과 | 처리 |
| :--- | :--- | :--- | :--- |
| R4 | 설정·스태시 재사용 | 중요 2, 사소 6 | 3건 수정(`f704b594d`): 설정을 검증하는 동안 다른 스레드가 검증 안 된 옛 설정을 받는 경쟁 등. 5건 기각 |
| R5 | R4 수정분 | 차단 결함 없음 | 낮음 3·사소 4 는 수정하지 않음(테스트 코드, 기존 동작) |
| R6 | 상주 `cat-file` | 높음 1, 중간 2, 낮음 6 | 7건 수정(`c0cc661ef`), 아래 보안 항목 포함 |
| R7 | 열기 호출 정리, index 갱신 | 차단 결함 없음. 중간 1, 낮음 10 | **미수정** — 다음 릴리스에서 3건 수정 예정(잠금 없이 기록하는 플래그, 컨트롤 해제 시 대기 취소, 취소된 워크트리 로드의 늦은 상태 변경). 모두 결과가 틀리는 경우는 아님 |
| R8 | R6 수정분 | 차단 결함 없음. 중간 2, 낮음 5 | **미수정** — 다음 릴리스에서 수정 예정(아래) |

**보안 항목 (R6 H1, 수정됨)**: 상주 프로세스는 저장소 폴더를 잠그지 않으려고 `--git-dir` 로 시작하는데, 이 방식은 git 의 저장소 소유권 검사(`safe.directory`)를 건너뛴다. 저장소 설정이 include 하는 파일에 promisor 원격과 `core.sshCommand` 를 넣으면, 없는 객체를 조회할 때 그 명령이 실행되는 것을 리뷰에서 재현했다. 수정:
- 상주 프로세스를 `--no-lazy-fetch` 로 실행한다(이 옵션이 있는 git 2.45 이상에서만 상주 프로세스를 쓴다). 재리뷰에서 실행이 막히는 것을 확인했다.
- git 이 그 저장소의 로컬 설정을 받아들였을 때만 상주 프로세스를 쓴다(소유자가 다른 저장소는 제외된다).
- 파일 문자열이 아니라 include 가 반영된 유효 설정으로 판정한다: promisor 원격, partial clone, 알 수 없는 `extensions.*`, 저장소 범위의 `core.sshCommand` 가 있으면 쓰지 않는다.

**R8 에서 남은 것 (미수정, 차단 아님)**:
- 설정을 읽은 기록에 파일 지문이 없는 경우, 낡은 설정으로 "상주 프로세스 사용 가능" 판정이 날 수 있다. 이 경우에도 명령 실행은 `--no-lazy-fetch` 로 막힌다.
- index 를 조회하는 요청 형식(`info :path`)은 `core.fsmonitor` 명령을 실행한다. 현재 코드는 이 형식의 요청을 만들지 않는다(커밋 id 가 항상 앞에 붙는다). 방어 코드는 다음 릴리스에 넣는다.
- 첫 요청이 취소되면 시작 중인 프로세스를 250 ms 만에 버린다(성능만 영향).

### 검증

- 커밋마다 조율자가 빌드와 테스트를 다시 실행했다. 릴리스 대상 `hj/main` 에서의 결과는 릴리스 커밋 메시지와 GitHub Release 를 본다.
- **원인을 확인하지 못한 간헐 실패 1건**: 단위 테스트 `ConfigSnapshotReuseTests.Config_should_be_read_again_if_global_config_changed` 가 넓은 범위(2132개)로 함께 돌렸을 때 1회 실패했다(`git config list` 1회 기대, 2회 발생). 해당 테스트 묶음만 5회 돌렸을 때는 모두 통과했다. 다른 테스트의 기록이 섞인 것인지 제품이 실제로 두 번 실행한 것인지 조사 중이다. 제품 쪽이라면 영향은 설정을 한 번 더 읽는 것(성능)이고 틀린 값을 쓰는 것은 아니다.
- 이 PC 의 기존 실패 2건(`RunBatchCommand_can_handle_max_length_arguments`, scoop git shim 의 명령줄 길이)은 그대로다.

### 다음 릴리스 예정

- 4번 2단계: `git check-attr` 도 상주 프로세스로 (파일당 git 프로세스 1 → 0)
- 7번: fsmonitor 진단과 유휴 데몬 자동 종료
- 새 설정 키의 설정 화면 노출
- R7, R8 리뷰의 남은 지적 수정
- 보류: 3번 2단계(ref 가 같으면 전환 시 `git log` 생략). gitextensions 크기 저장소에서 전환 373 ms → 170~270 ms 로 추정되나, upstream 과 공유하는 파일에 훅이 필요하다.

---

## 7.3.0-hj-7 (2026-11-24)

- upstream 7개 커밋 반영(`bd69886b7` 까지).
- **git 실제 바이너리 직접 실행**(`5aa44fb7a`): 래퍼(`cmd\git.exe`, scoop shim) 대신 `ucrt64\bin\git.exe` 등을 바로 실행한다. 호출당 약 83 ms → 50 ms. `MSYSTEM` 이 설정돼 있거나 `%USERPROFILE%\bin` 이 있거나 설치 구조를 알 수 없으면 래퍼를 그대로 쓴다. 설정 키 `UseDirectGitBinary`(기본 켬).
- **커밋 선택 시 git 4종을 메모리에서 계산**(`000adc84d`): `branch --contains`, `tag --contains`, `describe`, `branch --merged` 를 리비전 그리드가 이미 읽은 그래프에서 답한다. 열기 git 프로세스 15 / 16 → 11 / 12. 필터가 걸려 있거나 그래프가 잘렸으면 git 을 실행한다.
- upstream 자동 반영 워크플로(`hj-upstream-autosync`, 매일 03:17 UTC): 충돌 없이 검증을 통과하면 `hj/main` 갱신, 버전 증가, 릴리스까지 수행한다.
- 코드 리뷰 3회(R1~R3). `MSYSTEM` 이 설정된 환경에서 실제 바이너리가 PATH 를 잡지 않아 sh 훅이 실패하는 문제 등을 고쳤다.

## 7.3.0-hj-6 (2026-11-19)

- **리비전 그래프 수정**(`06b6ce38a`): 스태시 커밋이 두 번 추가되면 행 수가 실제보다 1 많아져 캐시 재구성이 끝없이 반복되고(초당 약 1만 회), 선택이 간헐적으로 비어 diff 가 표시되지 않던 문제.
- 예산 테스트가 초기 선택을 기다리고 앱 활성화를 허용량으로 계산하도록 고쳤다. 간헐 실패가 사라졌다.

## 그 이전

- hj-4 ~ hj-5: Ghostty 기반 Console 탭, 저장소 열기 git 호출 예산 도입(설정·ref·상태 결과 재사용).
- 상세는 `docs/hj/perf-analysis.md`, `docs/hj/perf-w28-report.md`.
