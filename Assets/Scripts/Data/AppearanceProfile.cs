using System;
using System.Collections.Generic;
using Unity.Netcode;

/// <summary>
/// 외형 특징 축 — 몽타주로 구두 전달 가능한 특징 종류.
/// 축별 옵션(표시 이름·시각 리소스)은 AppearanceDatabase가 정의한다.
/// </summary>
public enum AppearanceAxis
{
    HairStyle = 0,
    HairColor = 1,
    SkinColor = 2,
    FacialHair = 3,
    Headwear = 4,
    Eyewear = 5,
}

/// <summary>
/// NPC 한 명의 외형 특징 조합 — 축별 옵션 인덱스만 담는다.
/// CitizenIdentity의 일부로 취급하며, 네트워크로는 이 인덱스만 전송된다.
/// </summary>
[Serializable]
public struct AppearanceProfile : INetworkSerializable, IEquatable<AppearanceProfile>
{
    // 외형 축 개수 — AppearanceAxis enum 값 수와 일치해야 함.
    public const int k_axisCount = 6;

    public int HairStyleIndex;
    public int HairColorIndex;
    public int SkinColorIndex;
    public int FacialHairIndex;
    public int HeadwearIndex;
    public int EyewearIndex;

    // 아직 배정되지 않음(프리팹 기본 외형)을 뜻하는 값.
    public static AppearanceProfile Unassigned => new AppearanceProfile
    {
        HairStyleIndex = -1,
        HairColorIndex = -1,
        SkinColorIndex = -1,
        FacialHairIndex = -1,
        HeadwearIndex = -1,
        EyewearIndex = -1,
    };

    public bool IsAssigned => HairColorIndex >= 0;

    public int GetIndex(AppearanceAxis axis) => axis switch
    {
        AppearanceAxis.HairStyle => HairStyleIndex,
        AppearanceAxis.HairColor => HairColorIndex,
        AppearanceAxis.SkinColor => SkinColorIndex,
        AppearanceAxis.FacialHair => FacialHairIndex,
        AppearanceAxis.Headwear => HeadwearIndex,
        AppearanceAxis.Eyewear => EyewearIndex,
        _ => -1,
    };

    public void SetIndex(AppearanceAxis axis, int value)
    {
        switch (axis)
        {
            case AppearanceAxis.HairStyle: HairStyleIndex = value; break;
            case AppearanceAxis.HairColor: HairColorIndex = value; break;
            case AppearanceAxis.SkinColor: SkinColorIndex = value; break;
            case AppearanceAxis.FacialHair: FacialHairIndex = value; break;
            case AppearanceAxis.Headwear: HeadwearIndex = value; break;
            case AppearanceAxis.Eyewear: EyewearIndex = value; break;
        }
    }

    // 지정한 축들에서 다른 프로필과 값이 모두 같은지 — 몽타주 부합 판정에 사용.
    public bool MatchesOn(in AppearanceProfile other, IReadOnlyList<AppearanceAxis> axes)
    {
        for (int i = 0; i < axes.Count; i++)
        {
            if (GetIndex(axes[i]) != other.GetIndex(axes[i]))
                return false;
        }
        return true;
    }

    public void NetworkSerialize<TBuffer>(BufferSerializer<TBuffer> serializer) where TBuffer : IReaderWriter
    {
        serializer.SerializeValue(ref HairStyleIndex);
        serializer.SerializeValue(ref HairColorIndex);
        serializer.SerializeValue(ref SkinColorIndex);
        serializer.SerializeValue(ref FacialHairIndex);
        serializer.SerializeValue(ref HeadwearIndex);
        serializer.SerializeValue(ref EyewearIndex);
    }

    public bool Equals(AppearanceProfile other) =>
        HairStyleIndex == other.HairStyleIndex
        && HairColorIndex == other.HairColorIndex
        && SkinColorIndex == other.SkinColorIndex
        && FacialHairIndex == other.FacialHairIndex
        && HeadwearIndex == other.HeadwearIndex
        && EyewearIndex == other.EyewearIndex;

    public override bool Equals(object obj) => obj is AppearanceProfile other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        HairStyleIndex, HairColorIndex, SkinColorIndex,
        FacialHairIndex, HeadwearIndex, EyewearIndex);
}
