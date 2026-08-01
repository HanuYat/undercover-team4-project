public abstract class NpcStateBase
{
    protected readonly NpcController m_owner;

    protected NpcStateBase(NpcController owner)
    {
        m_owner = owner;
    }

    public abstract void Enter();
    public abstract void Tick();
    public abstract void Exit();
}
