using UnityEngine;

/// <summary>씬당 하나 배치되는 씬 매니저 베이스. 씬 진행·전환의 진입점.</summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class SceneManagerBase : CommonManagerBase
{
    public virtual void MoveToNextScene(EScene nextScene) => App.LoadScene(nextScene);
}
