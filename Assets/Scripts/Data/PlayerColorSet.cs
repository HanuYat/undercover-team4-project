using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 한 사람의 로봇 색 — 부위별 팔레트 인덱스 묶음 (#432).
///
/// 부위가 셋이라 값도 셋이지만 <b>따로 나르지 않는다</b>: 세 값은 항상 같이 바뀌고 같이 읽히므로,
/// 갈라 두면 한 부위만 먼저 도착한 중간 상태가 화면에 보인다. 로비 명부와 게임 씬
/// (<c>PlayerCosmetics</c>)이 이 묶음 하나를 그대로 나른다.
///
/// 색이 아니라 <b>인덱스</b>인 이유는 팔레트가 정본이기 때문이다 — 3바이트로 끝나고, 팔레트가
/// 바뀌어도 옛 값이 범위 밖으로 나갈 뿐 깨지지 않는다 (<see cref="PlayerColorPalette.Get"/>가 자른다).
/// </summary>
[Serializable]
public struct PlayerColorSet : INetworkSerializable, IEquatable<PlayerColorSet>
{
    public byte Head;
    public byte Torso;
    public byte Legs;

    public byte this[EBodyPart part]
    {
        get
        {
            switch (part)
            {
                case EBodyPart.Head:
                    return Head;
                case EBodyPart.Torso:
                    return Torso;
                default:
                    return Legs;
            }
        }
        set
        {
            switch (part)
            {
                case EBodyPart.Head:
                    Head = value;
                    break;
                case EBodyPart.Torso:
                    Torso = value;
                    break;
                default:
                    Legs = value;
                    break;
            }
        }
    }

    /// <summary>지금 내가 고른 색 — 값의 출처는 <see cref="GameSettings"/> 하나다.</summary>
    public static PlayerColorSet FromSettings() =>
        new PlayerColorSet
        {
            Head = ToIndex(GameSettings.GetPlayerColor(EBodyPart.Head)),
            Torso = ToIndex(GameSettings.GetPlayerColor(EBodyPart.Torso)),
            Legs = ToIndex(GameSettings.GetPlayerColor(EBodyPart.Legs)),
        };

    /// <summary>초상을 색 조합 단위로 캐시할 때 쓰는 열쇠 — 같은 조합이면 같은 얼굴이다.</summary>
    public int Key => Head | (Torso << 8) | (Legs << 16);

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Head);
        serializer.SerializeValue(ref Torso);
        serializer.SerializeValue(ref Legs);
    }

    public bool Equals(PlayerColorSet other) =>
        Head == other.Head && Torso == other.Torso && Legs == other.Legs;

    public override bool Equals(object obj) => obj is PlayerColorSet other && Equals(other);

    public override int GetHashCode() => Key;

    // 팔레트 길이는 여기서 보지 않는다 — 범위 밖 인덱스는 팔레트가 자르므로 모두가 같은 색을 본다
    private static byte ToIndex(int index) => (byte)Mathf.Clamp(index, 0, byte.MaxValue);
}
