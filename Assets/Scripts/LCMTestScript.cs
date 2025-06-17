using UnityEngine;
using LCM.LCM;
using System;
using Mujoco;

public class LCMTestScript : MonoBehaviour
{
    private LCM.LCM.LCM lcm;
    static sensor_msgs.JointState jointStateMessage;
    public GameObject urdfModel;
    public ArticulationBody[] articulationBodies;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    internal class SimpleSubscriber : LCM.LCM.LCMSubscriber
    {
        public void MessageReceived(LCM.LCM.LCM lcm, string channel, LCM.LCM.LCMDataInputStream ins)
        {
            // Handle incoming messages here
            if (channel == "joint_states#sensor_msgs.JointState")
            {
                jointStateMessage = new sensor_msgs.JointState(ins);
                // Debug.Log("Positions: " + string.Join(", ", jointStateMessage.position));
            }
            // sleep for a short time to simulate processing delay
            System.Threading.Thread.Sleep(1); // Sleep for 10 milliseconds
        }
    }
    void Start()
    {
        
        try
        {
            Debug.Log("Starting LCM initialization...");

            // Log information about URDF model and articulation bodies
            if (urdfModel == null)
                Debug.LogError("URDF Model reference is null!");
            else
                Debug.Log("URDF Model reference found: " + urdfModel.name);

            if (articulationBodies == null || articulationBodies.Length == 0)
                Debug.LogError("ArticulationBodies array is null or empty!");
            else
                Debug.Log("Found " + articulationBodies.Length + " articulation bodies");

            // Initialize LCM with a try-catch to catch any network issues
            lcm = new LCM.LCM.LCM("udpm://239.255.76.67:7667?ttl=1");

            // Set up subscriber
            lcm.SubscribeAll(new SimpleSubscriber());
            Debug.Log("LCM initialized and subscriber set up successfully.");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error during LCM initialization: " + ex.Message);
            Debug.LogException(ex);
        }
    }

    // Update is called once per frame
    void Update()
    {
        // jointStateMessage.header.stamp = new std_msgs.Time();
        // jointStateMessage.header.stamp.sec = (int)System.DateTime.UtcNow.Subtract(new System.DateTime(1970, 1, 1)).TotalSeconds;
        // jointStateMessage.header.stamp.nsec = (int)(System.DateTime.UtcNow.Ticks % 10000000) * 100; // Convert ticks to nanoseconds
        // jointStateMessage.header.seq++;
        // jointStateMessage.Encode(new LCMDataOutputStream());
        // lcm.Publish("joint_states", jointStateMessage);
        // Debug.Log("LCM message published: " + jointStateMessage.header.seq);
        // // Wait for a short time before publishing the next message
        // System.Threading.Thread.Sleep(1000); // Sleep for 1 second
        if (jointStateMessage != null && articulationBodies != null)
        {
            for (int i = 0; i < articulationBodies.Length && i < jointStateMessage.position.Length; i++)
            {
                // Set the target position for each articulation body
                var drive = articulationBodies[i].xDrive;
                if (i == 0)
                {
                    drive.target = (float)jointStateMessage.position[i];
                }
                else
                {
                    drive.target = (float)jointStateMessage.position[i] * Mathf.Rad2Deg; // Convert radians to degrees for Unity
                }
                articulationBodies[i].xDrive = drive;
            }
        }
    }
}
