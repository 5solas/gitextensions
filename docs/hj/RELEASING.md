# Git Extensions 포크(hj) 릴리스 가이드

본 문서는 Git Extensions hj 포크의 버전 명명 규칙, 릴리스 스크립트 실행 절차 및 현재 빌드 환경의 제약 사항을 설명합니다.

---

## 1. 버전 형식

- **문자 버전 (Informational/Display)**: `<BUILD_VERSION_BASE>-hj-<HjNumber>` (예: `7.3.0-hj-1`)
- **숫자 버전 (Assembly/File Version)**: `<BUILD_VERSION_BASE>.<HjNumber>` (예: `7.3.0.1`)
- **Git 태그 형식**: `hj/v<문자버전>` (예: `hj/v7.3.0-hj-1`)
- **업스트림 기준 버전 추종**: 기준 번호는 업스트림 워크플로(`.github/workflows/_app-build-core.yml`)의 `BUILD_VERSION_BASE`(현재 `7.3.0`)를 정규식으로 자동 추출하므로, 업스트림이 버전 번호를 올리면 자동으로 추종합니다.
- **태그 보존 특성**: Git 태그는 `hj/main`의 특정 커밋 객체에 고정(annotated tag)됩니다. 향후 새 동기화로 인해 `hj/main` 브랜치가 `master`에서 다시 생성되고 커밋 SHA가 달라지더라도, 이전에 부여된 태그는 불변(immutable)으로 유지되어 이전 빌드와 릴리스 지점을 영구적으로 보존합니다.

---

## 2. 릴리스 절차

새 릴리스를 제작하고 배포할 때는 다음 표준 절차를 순서대로 진행합니다:

1. **업스트림 동기화 및 `hj/main` 통합**:
   - 릴리스에 앞서 최신 업스트림 및 토픽 브랜치들의 통합 브랜치(`hj/main`)를 생성/갱신합니다:
     ```pwsh
     pwsh eng/hj/Sync-Upstream.ps1
     ```
   - 동기화 완료 후 통합 브랜치인 `hj/main`으로 체크아웃합니다:
     ```sh
     git checkout hj/main
     ```
2. **포크 버전 번호 증가**:
   - `eng/hj/VERSION` 파일을 열고 버전 번호를 1 증가시킵니다. (예: `1` -> `2`)
3. **릴리스 패키징 및 태그 생성 (`Release.ps1`)**:
   - 로컬 개발 환경(WiX/MSVC 부재)에서는 `-SkipMsi` 옵션을 지정하여 포터블 zip 번들을 빌드하고, `-Tag`로 릴리스 태그를 생성합니다:
     ```pwsh
     pwsh eng/hj/Release.ps1 -Configuration Release -SkipMsi -Tag
     ```
   - 어셈블리 빌드 및 버전 주입만 검증할 때는 `-SkipPublish`를 사용할 수 있습니다:
     ```pwsh
     pwsh eng/hj/Release.ps1 -Configuration Release -SkipPublish
     ```
   - 작업 트리에 커밋되지 않은 변경 사항이 있을 때는 `-AllowDirty`를 추가합니다.
   - `-Tag` 옵션은 현재 커밋에 `hj/v<BUILD_VERSION_BASE>-hj-<HjNumber>` 태그를 부착하며, 태그 메시지에 업스트림 베이스(`upstream-base: <merge-base commit>`) 정보를 자동으로 기록합니다.
4. **산출물 확인**:
   - 포터블 패키지 zip: `artifacts/Release/publish/GitExtensions-Portable-x64-<BUILD_VERSION_BASE>.<HjNumber>-hj-<HjNumber>-<GitCommit>.zip` (예: `GitExtensions-Portable-x64-7.3.0.1-hj-1-b8c365975.zip`)
   - 컴파일된 실행 파일: `artifacts/Release/bin/GitExtensions/net10.0-windows/GitExtensions.exe` 및 `GitExtensions.dll`
   - 버전 정보 확인:
     ```pwsh
     (Get-Item artifacts/Release/bin/GitExtensions/net10.0-windows/GitExtensions.dll).VersionInfo
     ```
   - 스탬프 원복 확인: 스크립트 실행 후 `git status --porcelain`에 `CommonAssemblyInfo.cs`나 서브모듈의 변경 사항이 남지 않고 원복되었는지 확인합니다.
5. **원격 푸시 및 GitHub Release 업로드**:
   - 생성된 태그와 `hj/main`을 원격 저장소에 푸시합니다:
     ```sh
     git push origin hj/main
     git push origin <태그명>
     ```
   - 제작된 포터블 zip 아티팩트를 GitHub Release에 업로드하여 배포합니다. (코디네이터가 관리하는 자동화 스크립트가 별도로 준비되어 있습니다.)

---

## 3. 현재 환경의 빌드 제약 사항

1. **네이티브 C++ 빌드 도구 부재 (MSVC)**:
   - 현재 개발 환경에는 Visual Studio C++ 도구 체인(MSVC)이 설치되어 있지 않으므로 `src/native/build.proj` 빌드가 수행되지 않습니다.
   - 따라서 `GitExtensionsShellEx` (셸 확장 dll) 및 `GitExtSshAskPass` (SSH 패스워드 프롬프트 exe) 등의 네이티브 C++ 바이너리는 빌드되지 않습니다.
2. **설치 패키지(MSI) 빌드 (`-SkipMsi` 필요)**:
   - 현재 로컬 개발 머신에는 WiX 툴체인 및 MSVC 네이티브 C++ 산출물이 없으므로 로컬에서는 `-SkipMsi` 옵션을 사용하여 포터블 zip 번들을 제작합니다.
   - 포터블 zip 및 publish 산출물에는 Ghostty Windows 네이티브 런타임(`LibGhostty.Net.dll`, `runtimes/win-x64/native/*`)이 정상 포함되며, `Product.wxs` 선언 및 번들 일관성 검사(`_CheckBundlesConsistent`)를 통과합니다.
   - 완전한 설치 패키지(MSI) 제작은 MSVC 및 WiX 환경이 갖춰진 CI 환경에서 수행합니다.
