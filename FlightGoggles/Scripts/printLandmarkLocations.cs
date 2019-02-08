using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using UnityEditor;
using System.IO;

public class printLandmarkLocations : MonoBehaviour
{
    

    // Start is called before the first frame update
    void Start()
    {

        Dictionary<string, List<GameObject>> GateMarkers = new Dictionary<string, List<GameObject>>();


        // Find all landmarks and print to file.
        foreach (GameObject obj in GameObject.FindGameObjectsWithTag("IR_Markers"))
        {
            // Tag the landmarks
            string gateName = obj.transform.parent.parent.name;
            string landmarkID = obj.name;

            // Check if gate already exists.
            if (GateMarkers.ContainsKey(gateName)){
                GateMarkers[gateName].Add(obj);
            } else {
                List<GameObject> markerList = new List<GameObject>();
                markerList.Add(obj);
                GateMarkers.Add(gateName, markerList);
            }
        }

        // Print results
        //Write some text to the test.txt file
        StreamWriter writer = new StreamWriter("Assets/Resources/markerLocations.txt", false);
        foreach (var pair in GateMarkers)
        {
            writer.WriteLine(pair.Key + ":");
            writer.Write("  location: [");

            int i = 0;
            foreach (GameObject marker in pair.Value)
            {

                // Convert vector from EUN to NWU.
                Vector3 NWU = new Vector3(marker.transform.position.z, -marker.transform.position.x, marker.transform.position.y);

                // Print out locations in yaml format.

                writer.Write("["+ NWU.x + ", " + NWU.y + ", " + NWU.z + "]");
                if (i < 3)
                {
                    writer.Write(", ");
                } else
                {
                    writer.WriteLine("]");
                }
                i++;
            }
            
        }
        writer.Close();

    }

    // Update is called once per frame
    void Update()
    {

    }


}
