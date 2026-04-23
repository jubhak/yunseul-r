# 화면 녹화 프로그램 (Screen Recorder)

Windows 화면의 특정 영역을 녹화하는 프로그램입니다.

## 주요 기능

- **영역 선택 녹화**: 마우스 드래그로 원하는 영역만 선택하여 녹화
- **이전 영역 기억**: 한번 지정한 영역은 다음 실행 시에도 유지
- **시스템 오디오 녹음**: PC에서 발생하는 소리를 함께 녹음
- **빠른 탐색(Seek)**: 2시간 이상 녹화해도 어떤 구간이든 1초 이내 즉시 재생
- **자동 파일명**: `history/yyyyMMdd-HHmm.mp4` 형식, 중복 시 `_2`, `_3` 접미사

## 빠른 Seek 원리

H.264 코덱에서 키프레임(I-frame) 간격을 1초(30프레임)로 설정합니다.
일반적인 녹화 프로그램은 키프레임 간격이 5~10초여서 탐색 시 지연이 발생하지만,
이 프로그램은 1초 간격으로 키프레임을 삽입하여 어떤 위치든 즉시 재생됩니다.

추가로 `faststart` 옵션으로 MP4의 메타데이터(moov atom)를 파일 앞에 배치하여
파일을 열자마자 탐색이 가능합니다.

## 사전 요구사항

### 1. .NET 8.0 Runtime
- https://dotnet.microsoft.com/download/dotnet/8.0

### 2. FFmpeg
- https://ffmpeg.org/download.html
- [gyan.dev 빌드](https://www.gyan.dev/ffmpeg/builds/) 또는 [BtbN 빌드](https://github.com/BtbN/FFmpeg-Builds/releases) 권장
- `ffmpeg.exe`를 프로그램 폴더에 넣거나 시스템 PATH에 추가

### 3. 시스템 오디오 녹음 (선택사항)
시스템 오디오를 녹음하려면 다음 중 하나가 필요합니다:
- **VB-Audio Virtual Cable** (무료): https://vb-audio.com/Cable/
- **virtual-audio-capturer** (Screen Capturer Recorder): https://github.com/rdp/screen-capture-recorder-to-video-windows-free

시스템 오디오 캡처 장치가 없으면 화면만 녹화됩니다.

## 빌드 및 실행

```bash
cd ScreenRecorder
dotnet restore
dotnet build
dotnet run
```

## 사용 방법

1. 프로그램 실행
2. **[녹화 시작]** 버튼 클릭
3. 영역이 지정되지 않았으면 영역 선택 화면이 표시됨
   - 마우스로 드래그하여 녹화할 영역 선택
   - 이전에 지정한 영역이 있으면 "이전 영역 사용" 버튼 표시
   - ESC로 취소
4. 녹화 진행 (녹화 시간 표시)
5. **[녹화 중지]** 버튼으로 녹화 종료
6. `history` 폴더에 MP4 파일 생성

## 파일 구조

```
ScreenRecorder/
├── history/                    # 녹화 파일 저장 폴더 (자동 생성)
│   ├── 20260423-1430.mp4
│   ├── 20260423-1430_2.mp4    # 같은 시간에 두 번째 녹화
│   └── ...
├── recording_settings.json     # 영역 설정 저장 파일
├── ffmpeg.exe                  # FFmpeg 실행 파일 (직접 배치)
└── ScreenRecorder.exe
```
