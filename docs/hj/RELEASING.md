# Git Extensions 포크(hj) 릴리스 가이드

본 문서는 Git Extensions hj 포크의 버전 명명 규칙, 릴리스 스크립트 실행 절차 및 현재 빌드 환경의 제약 사항을 설명합니다.

---

## 1. 버전 형식

- **문자 버전 (Informational/Display)**: `<BUILD_VERSION_BASE>-hj-<HjNumber>` (예: `7.3.0-hj-1`)
- **숫자 버전 (Assembly/File Version)**: `<BUILD_VERSION_BASE>.<HjNumber>` (예: `7.3.0.1`)
- **Git 태그 형식**: `hj/v<문자버전>` (예: `hj/v7.3.0-hj-1`)
- **업스트림 기준 버전 추종**: 기준 번호는 업스트림 워크플로(`.github/workflows/_app-build-core.yml`)의 `BUILD_VERSION_BASE`(현재 `7.3.0`)를 정규식으로 자동 추출하므로, 업스트림이 버전 번호를 올리면 자동으로 추종합니다.

---

## 2. 릴리스 절차

1. **포크 버전 번호 증가**:
   - `eng/hj/VERSION` 파일을 열고 버전 번호를 1 증가시킵니다. (예: `1` -> `2`)
2. **릴리스 스크립트 실행**:
   - **포터블 zip 패키징 제작 (-SkipMsi)**:
     ```pwsh
     pwsh eng/hj/Release.ps1 -Configuration Release -SkipMsi
     ```
   - **어셈블리 빌드만 수행할 때 (-SkipPublish)**:
     ```pwsh
     pwsh eng/hj/Release.ps1 -Configuration Release -SkipPublish
     ```
   - 작업 트리에 커밋되지 않은 변경 사항이 있을 때는 `-AllowDirty`를 추가합니다.
   - 태그를 함께 생성하려면 `-Tag` 옵션을 지정합니다. (annotated tag 메시지에 `upstream-base: <merge-base commit>` 자동 포함)
3. **산출물 확인**:
   - 포터블 패키지 zip: `artifacts/Release/publish/GitExtensions-Portable-x64-<BUILD_VERSION_BASE>.<HjNumber>-hj-<HjNumber>-<GitCommit>.zip` (예: `GitExtensions-Portable-x64-7.3.0.1-hj-1-b8c365975.zip`)
   - 컴파일된 실행 파일: `artifacts/Release/bin/GitExtensions/net10.0-windows/GitExtensions.exe` 및 `GitExtensions.dll`
   - 버전 정보 확인:
     ```pwsh
     (Get-Item artifacts/Release/bin/GitExtensions/net10.0-windows/GitExtensions.dll).VersionInfo
     ```
   - 스탬프 원복 확인: 스크립트 실행 후 `git status --porcelain`에 `CommonAssemblyInfo.cs`나 서브모듈의 변경 사항이 남지 않고 원복되었는지 확인합니다.

---

## 3. 현재 환경의 빌드 제약 사항

1. **네이티브 C++ 빌드 도구 부재 (MSVC)**:
   - 현재 개발 환경에는 Visual Studio C++ 도구 체인(MSVC)이 설치되어 있지 않으므로 `src/native/build.proj` 빌드가 수행되지 않습니다.
   - 따라서 `GitExtensionsShellEx` (셸 확장 dll) 및 `GitExtSshAskPass` (SSH 패스워드 프롬프트 exe) 등의 네이티브 C++ 바이너리는 빌드되지 않습니다.
2. **설치 패키지(MSI) 빌드 (`-SkipMsi` 필요)**:
   - 현재 로컬 개발 머신에는 WiX 툴체인 및 MSVC 네이티브 C++ 산출물이 없으므로 로컬에서는 `-SkipMsi` 옵션을 사용하여 포터블 zip 번들을 제작합니다.
   - 포터블 zip 및 publish 산출물에는 Ghostty Windows 네이티브 런타임(`LibGhostty.Net.dll`, `runtimes/win-x64/native/*`)이 정상 포함되며, `Product.wxs` 선언 및 번들 일관성 검사(`_CheckBundlesConsistent`)를 통과합니다.
   - 완전한 설치 패키지(MSI) 제작은 MSVC 및 WiX 환경이 갖춰진 CI 환경에서 수행합니다.
