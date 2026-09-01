using System;

// 미니맵 표시 대상 분류 (#835) — 휴대용 미니맵이 이 중 일부만 골라 보여준다.
[Flags]
public enum EMinimapMarker
{
    None = 0,
    Player = 1 << 0,
    Hq = 1 << 1,
    Event = 1 << 2,
    Criminal = 1 << 3,
    Door = 1 << 4,
    Cctv = 1 << 5,
}
