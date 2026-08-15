# DigitalTwin.Dashboard

픽앤플레이스 장비의 **Soft-PLC 겸 감시 대시보드**입니다. 가상 PLC 루프가 목표 100Hz로 3축 위치·속도를 산출하고, 이를 단일 상태 저장소(`DeviceTable`)에 기록한 뒤 **SLMP 3E**, **OPC UA**, **Named Pipe** 세 갈래로 동시에 노출합니다.

실제 PLC 하드웨어 없이도 상위 시스템(SCADA, MES, HMI, 3D 트윈)이 붙어 통신 검증을 할 수 있는 것이 목표입니다. 3D 시각화 클라이언트는 [DigitalTwin_PickAndPlace](https://github.com/udune/DigitalTwin_PickAndPlace) 리포에 있습니다.

---

## 아키텍처

```
                          ┌──────────────────────────┐
   Unity 3D ──Named Pipe──┤                          ├──→ SLMP 3E Server   :5007
                          │       DeviceTable        │     (미쓰비시 호환 D/M 디바이스)
   VirtualPLC ──~64Hz*───→┤    (single source of     │
                          │         truth)           ├──→ OPC UA Server    :4840
   ErrorDetector ←────────┤   불변 Snapshot pull     │     (opc.tcp, 19 노드)
                          │                          │
                          └────────────┬─────────────┘
                                       │ 30Hz 폴링
                                       ▼
                                   WPF UI (MVVM)
```

\* 제어 루프의 목표 주기는 100Hz지만 Windows 타이머 해상도(~15.6ms)에 눌려 실제로는 ~64Hz로 돕니다.
이동량·속도는 이 실제 주기를 재서 계산하므로 명령한 mm/s는 그대로 지켜집니다. ([아래 참조](#가상-plc))

### 설계 원칙

**1. 단일 진실(single source of truth)**
모든 상태는 `DeviceTable` 하나에만 존재합니다. UI, Unity 송신, SLMP, OPC UA는 각자 상태를 들고 있지 않고 `Snapshot()`으로 한 시점을 통째로 복사해 갑니다. 반환값은 불변 `record struct`라 소비 측에서 필드 티어링(일부 필드만 갱신된 상태를 읽는 문제)이 발생하지 않습니다.

**2. Push가 아닌 Pull**
`DeviceTable`에는 변경 통지 이벤트가 없습니다. 제어 루프 주기로 이벤트를 뿌리면 구독자 수만큼 초당 수백 건이 UI 스레드로 쏟아지기 때문에, 소비자가 각자 필요한 주기로 당겨가는 방식을 택했습니다. 그 결과 **제어 루프와 UI 갱신 30Hz가 완전히 분리**됩니다.

**3. 프로토콜 어댑터는 순수 부가물**
SLMP·OPC UA 서버는 `DeviceTable`만 참조하는 어댑터로 추가됐습니다. 기존 제어 로직은 한 줄도 수정하지 않았고, 서버를 제거해도 나머지가 그대로 동작합니다.

**4. 락 전략**
`DeviceTable`의 모든 접근은 단일 `lock` 아래에서 이뤄집니다. 크리티컬 섹션이 필드 복사뿐이라 마이크로초 단위로 끝나므로, `ReaderWriterLockSlim`은 과한 선택으로 판단했습니다.

---

## 주요 기능

### 가상 PLC
- 목표 100Hz 업데이트 루프 (`Task.Delay` 기반 비동기)
- `MoveTowards` 등속 보간, 최대 속도 `MaxSpeed` 설정값 (기본 100mm/s)
- 물리적 이동 한계(travel clamp)와 알람 경계를 분리해 관리
- 실제 이동 거리로부터 속도 역산

**루프 주기는 목표치이지 보장치가 아닙니다.** Windows 기본 타이머 해상도가 약 15.6ms라
`Task.Delay(10)`는 실제로 ~15.5ms 걸리고, 루프는 100Hz가 아니라 **약 64Hz**로 돕니다
(이 리포 개발 환경 실측: 64.5Hz / 평균 주기 15.51ms).
그래서 이동량과 속도는 가정한 주기(1/100초)가 아니라 `Stopwatch`로 잰 **실제 경과 시간**으로 계산합니다.
가정값을 쓰면 명령 속도 100mm/s가 실제로는 ~64mm/s로 움직이면서 화면에는 100으로 표시되는
1.55배 부풀림이 생기고, 사이클 타임 통계도 같이 틀어집니다.
루프가 오래 멈췄다 재개할 때 축이 순간이동하지 않도록 경과 시간은 100ms로 상한을 둡니다.

### 오류 감지
제어 루프가 위치를 기록한 직후 매 회차 UI 스레드 밖에서 판정합니다. 같은 오류는 기본 30초 간격으로만 재기록되며, 이 간격은 UI 슬라이더로 조정할 수 있습니다.

| 오류 | 조건 | 레벨 | 코드 |
|---|---|---|---|
| X축 리미트 초과 | X < XMin 또는 X > XMax | Error | `X_LIMIT` |
| Y축 리미트 초과 | Y < YMin 또는 Y > YMax | Error | `Y_LIMIT` |
| Z축 범위 초과 | Z < ZMin 또는 Z > ZMax | Error | `Z_LIMIT` |
| 안전 높이 미달 | Z < -30mm 이면서 XY 이동 중 | Warning | `Z_SAFE_HEIGHT` |
| 축 과속 | \|V\| > `AlarmMaxVelocity` (기본 150mm/s) | Warning | `*_OVERSPEED` |
| 과속 설정 불일치 | `AlarmMaxVelocity` ≥ `MaxSpeed` (시작 시 1회) | Warning | `CONFIG_OVERSPEED_UNREACHABLE` |

경계값은 `appsettings.json`으로 외부화돼 있고, 런타임 중 SLMP/OPC UA를 통해서도 변경할 수 있습니다.

**과속 경보의 도달 조건.** 속도는 `MoveTowards`가 자른 실제 이동 거리에서 역산되므로 \|V\|는 항상 `MaxSpeed` 이하입니다.
따라서 `AlarmMaxVelocity`를 `MaxSpeed` 이상으로 두면 과속 경보는 구조적으로 발생할 수 없습니다.
**기본값(100 / 150)이 바로 그 경우**이며, 이때 대시보드는 시작 시 `CONFIG_OVERSPEED_UNREACHABLE` 경고를 한 번 올려 이 사실을 알립니다.
과속 경보를 실제로 쓰려면 `AlarmMaxVelocity`를 `MaxSpeed`보다 낮추거나 `MaxSpeed`를 임계 위로 올리십시오.

### 대시보드 UI
- 3축 현재 위치·속도 실시간 표시 (0.1mm)
- LiveCharts 기반 축 위치 추이 차트
- 알람 그룹화 — 동일 오류를 `×N`으로 묶고 발생 시간 범위 표시
- 알람 이력 CSV 내보내기 (`exports/` 디렉터리, RFC 4180 이스케이프 처리)
- 사이클 자동 카운팅 및 평균 사이클 타임 (Z축 상·하한 통과 감지)
- 스텝 단위 수동 조작 (0.1 / 0.5 / 1.0mm / 커스텀)
- 원점 복귀, PLC·Unity 연결 상태 표시

---

## 산업 프로토콜 인터페이스

### SLMP 3E (TCP 5007)

미쓰비시 SLMP 3E 프레임을 직접 파싱·응답합니다. 실수값은 ×10 스케일 `short`로 인코딩됩니다 (예: 12.3mm → 123).

| 디바이스 | 내용 | 접근 |
|---|---|---|
| D0 ~ D2 | 현재 위치 X / Y / Z | R |
| D3 ~ D5 | 현재 속도 X / Y / Z | R |
| D6 ~ D8 | 타겟 위치 X / Y / Z | R/W |
| D10 ~ D11 | X축 알람 경계 Min / Max | R/W |
| D12 ~ D13 | Y축 알람 경계 Min / Max | R/W |
| D14 ~ D15 | Z축 알람 경계 Min / Max | R/W |
| M100 | 에러 램프 (통합) | R |
| M101 ~ M103 | X / Y / Z 축 에러 플래그 | R |

**지원 명령**

| 명령 | 서브 | 동작 |
|---|---|---|
| `0x0401` | `0x0000` | Word Read |
| `0x1401` | `0x0000` | Word Write |
| `0x0401` | `0x0001` | Bit Read |
| `0x1401` | `0x0001` | Bit Write |

**엔드 코드**

| 코드 | 의미 |
|---|---|
| `0x0000` | 정상 |
| `0xC059` | 미지원 명령 / 미지원 디바이스 / 읽기 전용 영역 쓰기 / 맵 밖 주소 |
| `0xC051` | 요청 점 수 범위 초과 (최대 960워드) |

TCP 스트림 특성상 프레임이 쪼개져 도착하는 경우를 고려해 부분 수신 재조립을 구현했습니다.

### OPC UA (opc.tcp://localhost:4840)

OPC Foundation .NET Standard 스택 기반이며, `CustomNodeManager2`를 상속해 주소 공간을 직접 구성합니다. `PickPlace` 폴더 아래 19개 변수를 약 30Hz로 갱신합니다.

| 노드 | 타입 | 접근 |
|---|---|---|
| `CurrentX` / `CurrentY` / `CurrentZ` | Double | R |
| `VelocityX` / `VelocityY` / `VelocityZ` | Double | R |
| `TargetX` / `TargetY` / `TargetZ` | Double | R/W |
| `XMin` / `XMax` / `YMin` / `YMax` / `ZMin` / `ZMax` | Double | R/W |
| `ErrorLamp` / `XError` / `YError` / `ZError` | Boolean | R |

- Application URI: `urn:DigitalTwin:PickPlace:Server`
- UaExpert 등 표준 클라이언트로 브라우징 가능

### Named Pipe (`DigitalTwinPipe`)

Unity 3D 뷰어와의 양방향 JSON 채널입니다.

| 방향 | 타입 | 내용 |
|---|---|---|
| → Unity | `axis_data` | 위치·속도·타임스탬프 (제어 루프 매 회차, 실측 ~64Hz) |
| → Unity | `error` | 알람 발생 (source, level, message) |
| → Unity | `clear_all_errors` | 알람 일괄 해제 |
| ← Unity | `axis_data` | 키보드 조작 결과를 타겟으로 반영 (last-writer-wins) |

---

## 기술 스택

| 항목 | 버전 |
|---|---|
| .NET | 10.0 (Windows) |
| UI | WPF + MaterialDesignThemes 5.3.0 |
| MVVM | CommunityToolkit.Mvvm 8.4.0 |
| 차트 | LiveChartsCore.SkiaSharpView.WPF 2.0.0-rc5.2 |
| OPC UA | OPCFoundation.NetStandard.Opc.Ua.Server 1.5.378.152 |
| JSON | Newtonsoft.Json 13.0.4 |

---

## 빌드 및 실행

**요구사항**: Windows, .NET 10 SDK

```bash
git clone https://github.com/udune/DigitalTwin.Dashboard.git
cd DigitalTwin.Dashboard
dotnet run
```

앱이 뜨면 SLMP(5007)·OPC UA(4840) 서버는 즉시 리슨을 시작합니다.
**`START`를 눌러야** 가상 PLC 제어 루프와 Named Pipe 서버가 가동됩니다.

| 버튼 | 동작 |
|---|---|
| `START` | PLC 루프 + Unity IPC 시작 |
| `STOP` | PLC 루프 정지 |
| `원점 복귀` | 타겟을 (0,0,0)으로 설정, 도달 시 완료 표시 |
| `알람 클리어` | 알람 목록 초기화 + Unity에 해제 통보 |
| `알람 내보내기` | `exports/` 에 CSV 저장 |

---

## 프로토콜 자기검증

프로토콜 구현이 실제로 표준을 만족하는지 확인하는 콘솔 클라이언트를 별도 프로젝트로 두었습니다. 데모가 아니라 PASS/FAIL을 집계하는 검증 하네스입니다.

```bash
# WPF 앱을 먼저 실행하고 START를 누른 뒤
dotnet run --project SlmpTestClient
dotnet run --project OpcUaTestClient
```

**SLMP 검증 시나리오**

1. `D0~D2` Word Read — 현재 위치 확인
2. `D6~D8` Word Write 후 `D0~D2` 재독해 — 타겟 반영 확인
3. `M100` Bit Read
4. `D11`(XMax)을 현재 X 아래로 Write → `M100` ON 확인 (런타임 경계 변경 → 알람 유발)
5. 읽기 전용 / 맵 밖 주소 쓰기 → 0이 아닌 엔드 코드 반환 및 상태 불변 확인
6. 쪼개진 프레임 부분 수신 → 정상 파싱 확인

---

## 설정

`appsettings.json` (빌드 시 출력 디렉터리로 복사됨)

```json
{
  "XLimit": 500.0,      // 물리적 이동 한계 (travel clamp)
  "YLimit": 500.0,
  "ZMin": -100.0,
  "ZMax": 50.0,
  "AlarmXMin": -125.9,  // 알람 경계 (런타임 변경 가능)
  "AlarmXMax": 125.9,
  "AlarmYMin": -125.9,
  "AlarmYMax": 125.9,
  "AlarmZMin": -60.0,
  "AlarmZMax": 0.0,
  "MaxSpeed": 100.0,          // 보간 최대 속도 (mm/s) — |V|의 상한
  "AlarmMaxVelocity": 150.0   // 과속 경보 임계 (mm/s) — MaxSpeed 이상이면 도달 불가
}
```

물리적 한계와 알람 경계는 별개입니다. 전자는 축이 물리적으로 갈 수 있는 범위(클램프), 후자는 그 안에서 경보를 울릴 기준선입니다.

`MaxSpeed`와 `AlarmMaxVelocity`도 마찬가지로 짝을 이룹니다. 전자가 속도의 상한을 만들고 후자가 그 안에서 경보 기준선이 되므로,
후자를 전자 이상으로 두면 경보는 울리지 않습니다(시작 시 경고로 통지). `MaxSpeed`가 0 이하이면 축이 멈춰버리므로 기본값 100으로 되돌립니다.

---

## 프로젝트 구조

```
DigitalTwin.Dashboard/
├── Models/
│   ├── AxisData.cs          축 위치·속도 DTO
│   ├── AlarmData.cs         알람 (그룹화 정보 포함)
│   ├── DeviceConfig.cs      appsettings 바인딩
│   └── SystemStatus.cs      시스템 가동 상태
├── Services/
│   ├── DeviceTable.cs       단일 진실 + 불변 스냅샷
│   ├── VirtualPLC.cs        제어 루프 (목표 100Hz, 실측 경과 시간 기반)
│   ├── ErrorDetector.cs     경계 판정 및 알람 생성
│   ├── SlmpServer.cs        SLMP 3E 프레임 서버
│   ├── OpcUaServer.cs       OPC UA 서버 + 노드 매니저
│   └── UnityIPCService.cs   Named Pipe 서버
├── ViewModels/
│   └── MainViewModel.cs     커맨드 + 옵저버블 상태
├── SlmpTestClient/          프로토콜 검증 콘솔
├── OpcUaTestClient/
└── appsettings.json
```

---

## 알려진 제약

- 그리퍼(Pick/Place) 제어는 현재 Unity 측 키보드 입력으로만 가능합니다. Named Pipe 명령 채널은 Unity에 구현돼 있으나 대시보드 UI 연동은 미완입니다.
- SLMP·OPC UA 포트가 코드에 고정돼 있습니다. `appsettings.json`으로 옮길 예정입니다.
- OPC UA는 익명 접속만 지원하며 보안 정책(인증서, 서명·암호화)을 적용하지 않았습니다.
- 가상 PLC는 등속 이동 모델입니다. 가감속 프로파일은 미구현입니다.
- 제어 루프는 목표 100Hz에 도달하지 못하고 실제로는 ~64Hz로 돕니다. Windows 타이머 해상도 한계이며, 이동량·속도는 실측 경과 시간으로 보정되므로 정확도 문제는 없지만 **샘플링 밀도**는 목표의 약 2/3입니다. 진짜 100Hz가 필요하면 타이머 해상도를 올리는 별도 조치가 필요합니다.
- 기본 설정(`MaxSpeed` 100 / `AlarmMaxVelocity` 150)에서는 과속 경보가 발생하지 않습니다. 시작 시 `CONFIG_OVERSPEED_UNREACHABLE` 경고로 통지되며, 쓰려면 두 값을 조정해야 합니다.

## 로드맵

- [ ] 대시보드 Pick/Place 제어 UI
- [ ] 실제 미쓰비시 PLC 연동 (SLMP 클라이언트 모드)
- [ ] OPC UA 보안 정책 및 사용자 인증
- [ ] 알람·트렌드 이력 DB 적재
- [ ] 구조화 로깅 (Serilog)

## 연관 리포지토리

[DigitalTwin_PickAndPlace](https://github.com/udune/DigitalTwin_PickAndPlace) — Unity 3D 시각화 클라이언트 (Unity 6, URP, Cinemachine 3.1, UI Toolkit)
