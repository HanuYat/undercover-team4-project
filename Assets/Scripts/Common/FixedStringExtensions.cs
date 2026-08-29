using Unity.Collections;

/// <summary>
/// string → FixedString 변환 공용 유틸 (#258).
/// FixedString은 용량 초과 시 예외를 던지므로 전부 잘라 담는 방식으로 통일한다.
/// null은 빈 문자열로 취급한다.
/// </summary>
public static class FixedStringExtensions
{
    public static FixedString64Bytes ToFixed64(this string value)
    {
        var result = new FixedString64Bytes();
        result.CopyFromTruncated(value ?? string.Empty);
        return result;
    }
}
