using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InstructionsPanel : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (SyncPoseReceiver.synchronizationStage >= 1){
            this.gameObject.SetActive(false);
        }
        else{
            this.gameObject.SetActive(true);
        }
        
    }
    
}
