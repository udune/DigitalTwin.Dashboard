# Unity-WPF IPC Protocol Specification

## Overview
Unity와 WPF 간 Named Pipe를 통한 양방향 통신 프로토콜 사양서입니다.

---

## Connection Info

| 항목 | 값 |
|------|-----|
| Pipe Name | `DigitalTwinPipe` |
| Direction | 양방향 (InOut) |
| WPF 역할 | **Server** (NamedPipeServerStream) |
| Unity 역할 | **Client** (NamedPipeClientStream) |
| 데이터 형식 | JSON (한 줄씩, newline으로 구분) |

---

## Message Types

### 1. axis_data (축 위치 데이터)

**방향**: WPF → Unity, Unity → WPF (양방향)

```json
{
    "type": "axis_data",
    "data": {
        "x": 0.0,
        "y": 0.0,
        "z": 0.0,
        "velocityX": 0.0,
        "velocityY": 0.0,
        "velocityZ": 0.0,
        "timestamp": "2024-01-01T00:00:00.000Z"
    }
}
```

| 필드 | 타입 | 단위 | 설명 |
|------|------|------|------|
| x | float | mm | X축 위치 |
| y | float | mm | Y축 위치 |
| z | float | mm | Z축 위치 |
| velocityX | float | mm/s | X축 속도 (선택) |
| velocityY | float | mm/s | Y축 속도 (선택) |
| velocityZ | float | mm/s | Z축 속도 (선택) |
| timestamp | string | ISO 8601 | 타임스탬프 (선택) |

**WPF 구현 필요사항**:
- Unity에서 `axis_data`를 보낼 수 있으므로, WPF도 파이프에서 데이터를 **읽어야** 합니다.
- 현재 WPF가 쓰기만 하고 읽지 않으면 "Pipe is broken" 에러 발생

---

### 2. error (에러 메시지)

**방향**: WPF → Unity

```json
{
    "type": "error",
    "source": "XAxis",
    "errorType": "Error",
    "message": "Limit Exceeded: 5.50",
    "timestamp": "2024-01-01T00:00:00.000Z"
}
```

| 필드 | 타입 | 값 | 설명 |
|------|------|-----|------|
| source | string | "XAxis", "YAxis", "ZAxis" | 에러 발생 축 |
| errorType | string | "Error", "Warning" | 에러 타입 |
| message | string | - | 에러 메시지 |
| timestamp | string | ISO 8601 | 타임스탬프 |

---

### 3. clear_all_errors (모든 에러 클리어)

**방향**: WPF → Unity

```json
{
    "type": "clear_all_errors"
}
```

---

## WPF Server 구현 요구사항

### 1. 양방향 통신 지원

```csharp
// WPF Server 예시
var pipeServer = new NamedPipeServerStream(
    "DigitalTwinPipe",
    PipeDirection.InOut,  // 양방향 필수!
    1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous
);

var reader = new StreamReader(pipeServer);
var writer = new StreamWriter(pipeServer) { AutoFlush = true };

// 읽기 루프 (별도 Task로 실행)
while (true)
{
    string json = await reader.ReadLineAsync();
    if (json == null) break;

    // Unity에서 보낸 axis_data 처리
    ProcessMessage(json);
}
```

### 2. Unity에서 보낸 axis_data 처리

Unity에서 키보드로 조작하면 `axis_data`를 WPF로 전송합니다.
WPF는 이 데이터를 받아서 UI(슬라이더 등)에 반영해야 합니다.

```csharp
private void ProcessMessage(string json)
{
    var message = JsonConvert.DeserializeObject<IPCMessage>(json);

    if (message.type == "axis_data")
    {
        var axisData = JsonConvert.DeserializeObject<AxisDataWrapper>(json);

        // UI 업데이트 (Dispatcher 사용)
        Dispatcher.Invoke(() =>
        {
            XSlider.Value = axisData.data.x;
            YSlider.Value = axisData.data.y;
            ZSlider.Value = axisData.data.z;
        });
    }
}
```

---

## 축 매핑 정보

Unity에서 실제 3D 모델의 축 방향:

| 논리적 축 | Unity Transform | 이동 방향 | 조작 키 |
|-----------|-----------------|-----------|---------|
| X축 | xAxis | X 방향 (left/right) | 좌/우 화살표 |
| Y축 | yAxis | Z 방향 (forward/back) | 상/하 화살표 |
| Z축 | zAxis | Y 방향 (up/down) | W/S 키 |

---

## 주의사항

1. **"Pipe is broken" 에러 원인**
   - WPF가 Unity에서 보낸 데이터를 읽지 않으면 발생
   - WPF에서 반드시 읽기 루프를 구현해야 함

2. **JSON 형식**
   - 각 메시지는 한 줄로 전송 (newline으로 구분)
   - `WriteLine()` / `ReadLineAsync()` 사용

3. **스레드 안전성**
   - WPF UI 업데이트 시 `Dispatcher.Invoke()` 사용 필수
