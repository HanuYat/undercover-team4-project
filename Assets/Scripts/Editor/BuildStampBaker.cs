using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// 빌드에 커밋 sha를 굽는다 (#622, 진단 전용 — 참가 차단에는 안 쓴다).
public class BuildStampBaker : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    private const string k_resourcesDir = "Assets/Scripts/Network/Config/Resources";
    private const string k_stampPath = k_resourcesDir + "/BuildStamp.txt";

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        try
        {
            string sha = BuildStamp.ReadFromGit();
            Directory.CreateDirectory(k_resourcesDir);
            File.WriteAllText(k_stampPath, sha);
            AssetDatabase.ImportAsset(k_stampPath);
            Debug.Log($"[BuildStampBaker] 커밋 sha 구움 / sha: {sha}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning(
                $"[BuildStampBaker] sha 굽기 실패(무시, 빈 값으로 진행) / {e.Message}"
            );
        }
    }

    // 항상 지운다 — 에디터가 이 빌드의 낡은 값이 아니라 살아있는 git을 보게 해야 한다.
    public void OnPostprocessBuild(BuildReport report)
    {
        try
        {
            if (AssetDatabase.DeleteAsset(k_stampPath) && Directory.Exists(k_resourcesDir))
                AssetDatabase.DeleteAsset(k_resourcesDir);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[BuildStampBaker] 생성 파일 정리 실패(무시) / {e.Message}");
        }
    }
}
