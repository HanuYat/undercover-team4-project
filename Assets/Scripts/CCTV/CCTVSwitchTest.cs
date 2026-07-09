using UnityEngine;
using UnityEngine.InputSystem;

public class CCTVSwitchTest : MonoBehaviour
{
    [SerializeField]
    CCTVSwitcher m_switcher;

    void Update()
    {
        if (Keyboard.current == null)
            return;

        if (Keyboard.current.eKey.wasPressedThisFrame)
            m_switcher.SwitchNext();

        if (Keyboard.current.qKey.wasPressedThisFrame)
            m_switcher.SwitchPrev();
    }
}
