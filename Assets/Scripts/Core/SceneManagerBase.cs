using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>씬당 하나 배치되는 씬 매니저 베이스. 씬 진행·전환의 진입점.</summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class SceneManagerBase : CommonManagerBase
{
    public virtual void MoveToNextScene(EScene nextScene) => App.LoadScene(nextScene);

    /// <summary>
    /// 씬 고유의 초기화(런타임 스폰 등)가 끝날 때까지 대기한다 — 로딩 화면이 이걸 기다린 뒤 내려간다. (#403)
    /// 씬 파일에 배치된 오브젝트는 활성화 프레임에 이미 준비되므로 기본은 즉시 완료다.
    /// 활성화 후에도 프레임에 걸쳐 채워지는 것이 있는 씬만 override 한다.
    /// 무한 대기로 로딩 화면에 갇히지 않도록, override 시 자체 상한을 둘 것.
    /// </summary>
    public virtual UniTask WaitUntilReadyAsync(CancellationToken token) => UniTask.CompletedTask;
}
