using UnityEngine;
using UnityEngine.UI;
public class PanelsController : MonoBehaviour
{

    [SerializeField] private Button btn_Reg;
    [SerializeField] private Button btn_Log;
    [SerializeField] private GameObject pnl_Reg;
    [SerializeField] private GameObject pnl_Log;

    private void Awake()
    {

        if (btn_Reg != null) btn_Reg.onClick.AddListener(Btn_Dis_PnlReg);
        if (btn_Log != null) btn_Log.onClick.AddListener(Btn_Dis_PnlLog);
    }


    public void Btn_Dis_PnlReg()
    {
        pnl_Reg.SetActive(true);
    }

    public void Btn_Dis_PnlLog()
    {
        pnl_Log.SetActive(true);
    }
}//new edit ddd yyyy
