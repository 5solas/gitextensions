# upstream 자동 반영·자동 릴리스 (hj-upstream-autosync)

upstream(`gitextensions/gitextensions` `master`)에 새 커밋이 있고 **충돌이 없으며 검증을 통과하면** 사람 개입 없이 토픽 rebase → `hj/main` 재생성 → 검증 → `eng/hj/VERSION` +1 → 푸시 → 태그 → 릴리스 빌드까지 한다. 하나라도 실패하면 **아무것도 푸시하지 않고** `upstream-sync` 이슈로 보고한다.

구성: `.github/workflows/hj-upstream-autosync.yml`, `eng/hj/Invoke-AutoSync.ps1`(동기화·검증·푸시), `eng/hj/Update-SyncIssue.ps1`(이슈), `eng/hj/Find-SimilarUpstream.ps1` + `eng/hj/similar-watch.txt`(유사 변경 알림). `hj-release.yml` 에 `workflow_call` 을 추가했다.

## 1. 흐름

1. `sync` 작업(windows-latest, 최대 120분): fetch → `origin/master == upstream/master` 이면 `up-to-date`(열린 이슈 닫기) 후 종료.
2. 하루 1회 제한 확인 → 유사 변경 검사(`similar.md`).
3. `origin/hj/infra:eng/hj/topics.txt` 순서대로 `origin/<토픽>` 을 임시 브랜치 `tmp/autosync-*` 에서 upstream 위로 rebase(rerere 끔). 충돌이 하나라도 있으면 모든 토픽의 충돌 파일을 모아 `conflict`.
4. rebase 된 `hj/infra` 에 `chore(hj): bump to hj-<N>` 커밋: `eng/hj/VERSION`=N, `docs/hj/release-notes/<base>-hj-<N>.md`(upstream 커밋 목록 + 유사 변경 절; `hj-release` 가 릴리스 본문으로 씀).
5. `hj/main` = upstream + 토픽 `--no-ff` 머지(메시지 `Merge branch '<토픽>' into hj/main`), 조상 관계 확인.
6. 네이티브 + `dotnet build -c Release` → 테스트(2절) → 푸시 계획 계산.
7. 주석 태그(`Release.ps1 -Tag` 와 같은 형식 `Git Extensions <ver>` + `upstream-base: <sha>`) 후 **한 번의** `git push --atomic` 으로 master·바뀐 토픽·`hj/main`·태그를 함께 푸시.
8. `release` 작업: `uses: ./.github/workflows/hj-release.yml`(tag 전달) → MSI·zip 빌드·업로드.
9. `finalize` 작업(ubuntu): 릴리스 결과로 이슈를 닫거나 남긴다(6절).

모드(첫 단계 `Resolve mode`): schedule → `release` / 수동 실행 `dry_run=true`(기본) → `dry-run`(푸시·태그·릴리스·이슈 변경 없음, 푸시 명령을 요약에 출력, `git push --dry-run` 으로 인증·lease 점검) / 저장소 변수 `HJ_AUTO_RELEASE=false` → `report-only`(dry-run 과 같되 이슈로 보고).

**새 워크플로로 만든 이유**: 감시기(`hj-upstream-watch`)는 `contents: read` 로 충분하지만 자동 반영은 `contents: write` 와 다단계 작업(재사용 워크플로 호출, 마무리 작업)이 필요하다. 권한이 큰 쪽을 분리해 두고, 감시기는 수동 분석용으로 남겼다(schedule 제거, 같은 `concurrency` 그룹 `hj-upstream-sync` 로 이슈 동시 편집 방지).

## 2. 설계 결정과 근거

### 2.1 권한과 연쇄 실행
- **확인된 사실**(GitHub Docs, "Triggering a workflow from a workflow"): `GITHUB_TOKEN` 으로 일으킨 이벤트는 새 실행을 만들지 않는다. 예외는 `workflow_dispatch`·`repository_dispatch` 뿐. → `GITHUB_TOKEN` 으로 푸시한 태그는 `hj-release` 의 `on: push` 를 일으키지 않는다.
- **결정: `workflow_call`**. 추가 비밀값도 `actions: write` 도 필요 없고, 릴리스 빌드가 같은 실행의 작업이라 성공·실패를 `finalize` 가 바로 안다. `gh workflow run`(dispatch 예외)도 비밀값 없이 가능하지만 결과를 알려면 폴링이 필요해 택하지 않았다. `./` 호출은 **호출한 쪽 커밋의** `hj-release.yml` 을 쓰므로, 이 변경이 `hj/main` 에 들어가야 동작한다.
- **워크플로 파일 권한**: `permissions` 키 목록(Docs, workflow syntax)에 `workflows` 가 없어 `GITHUB_TOKEN` 에 줄 수 없다. 권한 없는 앱 토큰이 워크플로 파일을 "만들거나 바꾸는" 푸시는 `refusing to allow a GitHub App to create or update workflow ... without workflows permission` 으로 거부된다(커뮤니티 보고, 공식 문서로는 미확인). **GitHub 이 무엇을 비교하는지는 문서에 없다(미확인)**. 우리 `hj/main` 에는 매번 `hj/infra` 를 머지한 새 커밋(첫 부모 대비 `.github/workflows/hj-*.yml` 추가)이 생기고, rebase 된 `hj/infra` 에도 워크플로 파일을 만든 커밋 2개가 새로 생긴다. 새 커밋 단위로 검사한다면 **`GITHUB_TOKEN` 푸시는 매번 거부된다**. ref 전후 차이로 검사한다면 upstream 이 워크플로를 바꾼 날만 거부된다.
- **거부되었을 때**: `--atomic` 이므로 원격은 그대로이고, 상태 `push-rejected-workflows` 로 이슈를 남긴다(수동 `Sync-Upstream.ps1 -Push` 요청). 보고서의 푸시 계획 표에 ref 마다 "워크플로 차이 파일 수 / 워크플로를 바꾸는 새 커밋 수"를 적어 원인을 보이게 했다.
- **대안(선택)**: 비밀값 `HJ_SYNC_TOKEN`(이 저장소 한정 fine-grained PAT, Contents·Workflows 읽기/쓰기)이 있으면 checkout·푸시에 쓴다. PAT 로 푸시한 태그는 `hj-release` 를 직접 일으키므로 `workflow_call` 은 건너뛴다(`release-mode=tag-push`, 중복 빌드 방지). 이 경우 master 푸시가 upstream CI(`App build and test`)도 일으킨다.

### 2.2 검증 범위
- **차단**: 네이티브 빌드, `dotnet build -c Release`, `GitCommands.Tests`·`GitUI.Tests` 전체(`--blame-hang-timeout 3m`, upstream CI `_app-build-core.yml` 과 같은 옵션). upstream CI 는 windows-latest 에서 전체 테스트를 30분 제한 안에 돌리므로 upstream 테스트 자체는 러너에서 안정적이라고 본다.
- **보고만**: `UI.IntegrationTests` 중 `FormBrowseGitProcessBudgetTests`·`GhosttyConsoleTab`·`FormBrowseTests`. 포크 테스트이고 러너에서 돌려 본 적이 없다. git 프로세스 수를 세는 예산 테스트는 git 버전·타이밍에 민감하고 이전에 불안정했던 이력(hj-6 에서 수정)이 있다. 결과가 2주쯤 쌓여 안정적이면 `Invoke-AutoSync.ps1` 의 `-Blocking $false` 를 `$true` 로 바꾼다.
- **알려진 실패**: `RunBatchCommand_can_handle_max_length_arguments` 는 실패해도 경고로만 표에 남긴다(로컬 scoop git 래퍼 탓, 러너에서는 통과할 가능성이 높다). `-KnownTestFailures` 로 조정.
- 하지 않는 것: 번역 검증, publish/MSI(릴리스 단계에서 실패하면 `release-failed`).

### 2.3 원자성·안전
- 로컬 실 브랜치를 건드리지 않는다. 모든 결과는 `origin/*` 에서 만든 `tmp/autosync-*` 에 쌓고 명시적 refspec 으로 푸시한다.
- lease 값은 시작할 때 fetch 한 `origin/*` SHA. 마지막에 한 번 `git push --atomic --force-with-lease=refs/heads/<b>:<sha> …` 로 master·바뀐 토픽·`hj/main`·태그를 보낸다. git 문서상 `--atomic` 은 전부 갱신되거나 하나도 안 되며, 서버가 지원하지 않으면 푸시 자체가 실패한다. 따라서 "태그는 마지막"보다 강하게 반쯤 바뀐 상태가 없다. 바뀌지 않은 토픽은 푸시하지 않는다.
- 사람이 그사이 토픽이나 `hj/main` 을 푸시했으면 lease 거부 → `lease-rejected` 이슈, 다음 날 다시 시도.
- 푸시 후 릴리스 빌드만 실패하면 태그는 남고 이슈 `release-failed` 에 재실행 방법을 적는다(`hj/main` 은 이미 검증 통과).
- 동시 실행: `concurrency: hj-upstream-sync`, `cancel-in-progress: false`.

### 2.4 릴리스 과다 방지
- upstream 이 움직였을 때만 진행한다(토픽만 바뀐 경우는 지금처럼 수동 릴리스).
- 하루 최대 1회: 오늘(UTC 날짜)에 만들어진 `hj/v*` 태그가 있으면 `rate-limited`(이슈 변경 없음). 수동 실행의 `ignore_daily_limit` 으로만 넘긴다.
- 스위치: 저장소 변수 `HJ_AUTO_RELEASE` 가 `false` 이면 `report-only`. 없거나 다른 값이면 켜짐.

### 2.5 기존 스크립트 재사용
- `Sync-Upstream.ps1` 은 CI 에 쓰지 않는다. 충돌 시 rebase 를 멈춘 채 사람에게 넘기고(exit 2), 결과가 콘솔뿐이며, 로컬 브랜치·백업 ref 를 바꾸고, 푸시가 ref 별 순차(master 는 lease 없음)라 중간 실패 시 원격이 반쯤 바뀐다. 테스트 목록도 로컬용이고 VERSION·태그를 다루지 않는다. 플래그로 덧대면 동작이 두 갈래로 갈라지므로 `Invoke-AutoSync.ps1` 을 새로 두고 검증 규칙(`git cherry` 기반 커밋 수 확인, 조상 확인, `--no-ff` 순서)은 그대로 옮겼다. `Sync-Upstream.ps1` 은 바꾸지 않았고 수동 경로로 남는다.
- `Release.ps1` 은 실행하지 않는다(빌드·publish 는 `hj-release` 몫). 태그 메시지 형식과 버전 규칙(기준 번호는 upstream `_app-build-core.yml` 의 `BUILD_VERSION_BASE`)은 같다. N = max(`origin/hj/infra` 의 VERSION, 가장 큰 `-hj-N` 태그) + 1.
- 발견: upstream `.gitignore` 의 `[Rr]elease*/` 가 `docs/hj/release-notes/` 를 무시한다. 그래서 `hj-release` 가 읽는 이 경로는 지금까지 커밋할 수 없었다. 자동 반영은 `git add -f` 로 넣는다.

### 2.6 이슈 처리(`Update-SyncIssue.ps1`)
- "활성 이슈" = 열린 `upstream-sync` 이슈 중 `review` 라벨이 없는 것 하나. 같은 제목이면 본문만 갱신하고 댓글은 달지 않는다.
- `up-to-date` → 닫음. 실패(`conflict`/`build-failed`/`test-failed`/`lease-rejected`/`push-rejected-workflows`/`push-failed`/`error`) → 생성·갱신. 본문 첫 줄은 "아무것도 푸시하지 않았습니다", 이어서 보고서(충돌 파일, 실패 테스트 이름, 푸시 계획)를 싣는다.
- 성공(`finalize`): 유사 변경이 없으면 릴리스 링크를 남기고 닫는다. 있으면 닫지 않고 `review` 라벨을 붙인다. 제목은 `upstream 반영 완료 <tag> — 유사 변경 검토 필요`. 이 이슈는 사람이 닫으며, 다음 실행은 새 활성 이슈를 쓴다.

### 2.7 scoop 버킷
`5solas/scoop-hj` 는 다른 저장소다. `GITHUB_TOKEN` 권한은 워크플로가 있는 저장소로 한정된다(Docs, "GITHUB_TOKEN"). 그래서 여기서 갱신을 일으킬 수 없고, 버킷의 Excavator(6시간마다)가 새 릴리스를 따라간다. 최대 약 6시간 늦을 수 있다.

### 2.8 포크 패치와 유사한 upstream 변경
- `eng/hj/similar-watch.txt` 의 줄마다 `주제 이름 | 정규식 | 경로 글롭(쉼표)`. 정규식은 커밋 제목·본문, 글롭은 변경 파일과 대조한다. 필드 구분은 앞뒤 공백이 있는 ` | ` 이므로 정규식 안의 `|` 는 공백 없이 쓴다.
- 결과는 보고서·이슈·워크플로 요약·릴리스 노트에 "포크 패치와 유사한 upstream 변경" 절로 들어간다. 일치가 없으면 "없음". 감시기(`Watch-Upstream.ps1`) 보고서에도 같은 절을 넣었다.
- **키워드 대조는 오탐·누락이 있는 보조 수단이다.** 큰 리팩터링 커밋은 경로로 여러 주제에 걸린다. 판단은 사람이 한다.

## 3. 끄는 법과 수동 실행
- 끄기: Settings → Secrets and variables → Actions → Variables 에 `HJ_AUTO_RELEASE` = `false`. 완전히 멈추려면 Actions 탭에서 `hj-upstream-autosync` 를 Disable.
- 수동 점검: Actions → `hj-upstream-autosync` → Run workflow(`dry_run` 켬). upstream 이 안 움직였을 때는 `base_ref` 에 `upstream/master~5` 를 넣어 흐름 전체를 시뮬레이션한다(이때 `git push --dry-run` 점검은 생략).
- 수동 실제 실행: `dry_run` 끔(필요하면 `ignore_daily_limit`).
- 로컬 시뮬레이션(원격 변경 없음): `pwsh eng/hj/Invoke-AutoSync.ps1 -DryRun -BaseRef upstream/master~5 -SkipBuild`.
- 실패 후 복구: 이슈의 안내대로 `pwsh eng/hj/Sync-Upstream.ps1` → `-Push`, 필요하면 `Release.ps1 -Tag` 와 태그 푸시(RELEASING.md).

## 4. 확인하지 못한 것
- `GITHUB_TOKEN` 푸시의 워크플로 파일 검사 기준(2.1). 첫 실제 실행 결과로 판정한다. 거부되면 `HJ_SYNC_TOKEN` 을 쓸지 정해야 한다.
- GitHub 의 `--atomic` 푸시 지원. dry-run 의 `git push --dry-run --atomic` 이 통과하면 협상 단계는 된다는 뜻이다(서버 측 거부 훅까지는 검사하지 않음).
- 재사용 워크플로 안의 최상위 `concurrency` 동작, ubuntu 러너의 `pwsh` 기본 설치(runner-images 문서상 포함).
- 러너에서 `GitUI.Tests` 전체·통합 테스트 소요 시간과 안정성. 120분 제한이 충분한지.
- `actions/download-artifact` v4 와 `upload-artifact` v7 의 같은 실행 내 호환(기존 `hj-release` 가 같은 조합을 쓴다).
- 네이티브 빌드가 .NET 빌드·테스트에 꼭 필요한지(upstream CI 순서를 따랐다).
