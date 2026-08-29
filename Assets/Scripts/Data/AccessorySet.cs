using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 한 사람의 치장 — 슬롯별 카탈로그 인덱스 묶음 (#818). <see cref="PlayerColorSet"/>과 같은 모양이다.
/// <b>0은 "안 씀"</b>이라 기본값이 그대로 안전한 상태다 — 색과 달리 미배정 플래그가 필요 없다.
/// </summary>
[Serializable]
public struct AccessorySet : INetworkSerializable, IEquatable<AccessorySet>
{
    public byte Headwear;
    public byte FacialHair;
    public byte Hair;
    public byte Eyewear;
    public byte Facewear;
    public byte Earwear;

    public byte this[EAccessorySlot slot]
    {
        get
        {
            switch (slot)
            {
                case EAccessorySlot.Headwear:
                    return Headwear;
                case EAccessorySlot.FacialHair:
                    return FacialHair;
                case EAccessorySlot.Hair:
                    return Hair;
                case EAccessorySlot.Eyewear:
                    return Eyewear;
                case EAccessorySlot.Facewear:
                    return Facewear;
                default:
                    return Earwear;
            }
        }
        set
        {
            switch (slot)
            {
                case EAccessorySlot.Headwear:
                    Headwear = value;
                    break;
                case EAccessorySlot.FacialHair:
                    FacialHair = value;
                    break;
                case EAccessorySlot.Hair:
                    Hair = value;
                    break;
                case EAccessorySlot.Eyewear:
                    Eyewear = value;
                    break;
                case EAccessorySlot.Facewear:
                    Facewear = value;
                    break;
                default:
                    Earwear = value;
                    break;
            }
        }
    }

    /// <summary>지금 내가 고른 치장 — 값의 출처는 <see cref="GameSettings"/> 하나다.</summary>
    public static AccessorySet FromSettings()
    {
        var set = new AccessorySet();
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
            set[slot] = ToIndex(CosmeticLoadout.GetAccessory(slot));

        return set;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Headwear);
        serializer.SerializeValue(ref FacialHair);
        serializer.SerializeValue(ref Hair);
        serializer.SerializeValue(ref Eyewear);
        serializer.SerializeValue(ref Facewear);
        serializer.SerializeValue(ref Earwear);
    }

    public bool Equals(AccessorySet other) =>
        Headwear == other.Headwear
        && FacialHair == other.FacialHair
        && Hair == other.Hair
        && Eyewear == other.Eyewear
        && Facewear == other.Facewear
        && Earwear == other.Earwear;

    public override bool Equals(object obj) => obj is AccessorySet other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
            hash = (hash * 31) + this[slot];

        return hash;
    }

    // 카탈로그 길이는 여기서 보지 않는다 — 범위 밖 인덱스는 카탈로그가 null로 잘라 "안 씀"이 된다
    private static byte ToIndex(int index) => (byte)Mathf.Clamp(index, 0, byte.MaxValue);
}
