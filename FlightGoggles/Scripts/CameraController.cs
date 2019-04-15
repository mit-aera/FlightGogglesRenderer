/*
 * FlightGoggles Camera Controller.
 * Top-level logic script for FlightGoggles. 
 * Listens for ZMQ camera poses and object positions creates/moves cameras and objects to match.
 * Pushes rendered RGBD frames back to requester over ZMQ with associated metadata.
 * 
 * Author: Winter Guerra <winterg@mit.edu> 
 * Date: January 2017.
 */

// To allow for OBJ file import, you must download and include the TriLib Library
// into this project folder. Once the TriLib library has been included, you can enable
// OBJ importing by commenting out the following line.
#define TRILIB_DOES_NOT_EXIST

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Threading;
using System.Threading.Tasks;

// Array ops
using System.Linq;

// ZMQ/LCM
using NetMQ;

// Include JSON
using Newtonsoft.Json;

// Include message types
using MessageSpec;
using Unity.Collections;
using Unity.Jobs;
using System.IO;
using UnityEditor;

// Include postprocessing
//using UnityEngine.PostProcessing;

// TriLib dynamic model loader.
#if !TRILIB_DOES_NOT_EXIST
using TriLib;
using System.IO;
#endif

// Dynamic scene management
using UnityEngine.SceneManagement;

public class CameraController : MonoBehaviour
{
    // Default Parameters
    [HideInInspector]
    public const int pose_client_default_port = 10253;
    [HideInInspector]
    public const int video_client_default_port = 10254;
    [HideInInspector]
    public int pose_client_port = 10253;
    [HideInInspector]
    public int video_client_port = 10254;
    [HideInInspector]
    public const string client_ip_default = "127.0.0.1";
    [HideInInspector]
    public const string client_ip_pref_key = "client_ip";
    [HideInInspector]
    public const int connection_timeout_seconds = 2;
    [HideInInspector]
    public string flight_goggles_version = "";

    // Public Parameters
    public string client_ip = client_ip_default;
    public string obstacle_perturbation_file = "";
    public int max_num_ray_collisions = 1;
    
    public bool DEBUG = false;
    public bool outputLandmarkLocations = false;
    public GameObject camera_template;
    public GameObject splashScreen;

    // default scenes and assets
    public string topLevelSceneName;
    public string defaultSceneName;
    public string defaultLightingSceneName;

    // instance vars
    // NETWORK
    private NetMQ.Sockets.SubscriberSocket pull_socket;
    private NetMQ.Sockets.PublisherSocket push_socket;
    private bool socket_initialized = false;
    private StateMessage_t state;

    // Internal state & storage variables
    private UnityState_t internal_state;
    private bool is_first_frame;
    private Texture2D rendered_frame;
    private object socket_lock;

    /* =====================
     * UNITY PLAYER EVENT HOOKS 
     * =====================
     */

    // Function called when Unity Player is loaded.
    public IEnumerator Start()
    {
        // Make sure that this gameobject survives across scene reloads
        DontDestroyOnLoad(this.gameObject);

        // Get application version
        flight_goggles_version = Application.version;

        Debug.Log("FlightGoggles version: " + flight_goggles_version);

        // Fixes for Unity/NetMQ conflict stupidity.
        AsyncIO.ForceDotNet.Force();
        socket_lock = new object();
        
        // Instantiate sockets
        InstantiateSockets();

        // Check if previously saved ip exists
        client_ip = PlayerPrefs.GetString(client_ip_pref_key, client_ip_default);

        if (!Application.isEditor)
        {

            // Check if FlightGoggles should change its connection ports (for parallel operation)
            // Check if the program should use CLI arguments for IP.
            pose_client_port = Int32.Parse( GetArg("-input-port", "10253"));
            video_client_port = Int32.Parse(GetArg("-output-port", "10254"));


            // Check if the program should use CLI arguments for IP.
            string client_ip_from_cli = GetArg("-client-ip", "");
            if (client_ip_from_cli.Length > 0)
            {
                ConnectToClient(client_ip_from_cli);
            } else
            {
                ConnectToClient(client_ip);
            }

            // Check if the program should use CLI arguments for IP.
            obstacle_perturbation_file = GetArg("-obstacle-perturbation-file", "");
            
            // Disable fullscreen.
            Screen.fullScreen = false;
            Screen.SetResolution(1024, 768, false);

        } else
        {
            // Try to connect to the default ip
            ConnectToClient(client_ip);
        }
        
        // Init simple splash screen
        Text text_obj = splashScreen.GetComponentInChildren<Text>(true);
        InputField textbox_obj = splashScreen.GetComponentInChildren<InputField>(true);
        text_obj.text = "FlightGoggles Simulation Environment" + Environment.NewLine +
            flight_goggles_version + Environment.NewLine + Environment.NewLine +
            "Waiting for connection from client...";
        textbox_obj.text = client_ip;
        
        splashScreen.SetActive(true);
        
        // Initialize Internal State
        internal_state = new UnityState_t();

        // Do not try to do any processing this frame so that we can render our splash screen.
        internal_state.screenSkipFrames = 1;

        // Wait until end of frame to transmit images
        while (true)
        {
            // Wait until all rendering + UI is done.
            // Blocks until the frame is rendered.
            yield return new WaitForEndOfFrame();

            // Check if this frame should be rendered.
            if (internal_state.readyToRender)
            {
                // Read the frame from the GPU backbuffer and send it via ZMQ.
                sendFrameOnWire();
            }
        }
    }

    // Function called when Unity player is killed.
    private void OnApplicationQuit()
    {
        // Close ZMQ sockets
        pull_socket.Close();
        push_socket.Close();
        Debug.Log("Terminated ZMQ sockets.");
        NetMQConfig.Cleanup();

    }

    void InstantiateSockets()
    {
        // Configure sockets
        Debug.Log("Configuring sockets.");
        pull_socket = new NetMQ.Sockets.SubscriberSocket();
        pull_socket.Options.ReceiveHighWatermark = 6;

        // Setup subscriptions.
        pull_socket.Subscribe("Pose");
        push_socket = new NetMQ.Sockets.PublisherSocket();
        push_socket.Options.Linger = TimeSpan.Zero; // Do not keep unsent messages on hangup.
        push_socket.Options.SendHighWatermark = 6; // Do not queue many images.
       
    }

    public void ConnectToClient(string inputIPString)
    {

        Debug.Log("Trying to connect to: " + inputIPString);

        string pose_host_address = "tcp://" + inputIPString + ":" + pose_client_port.ToString();
        string video_host_address = "tcp://" + inputIPString + ":" + video_client_port.ToString();
        
        // Close ZMQ sockets
        pull_socket.Close();
        push_socket.Close();
        Debug.Log("Terminated ZMQ sockets.");
        NetMQConfig.Cleanup();

        // Reinstantiate sockets
        InstantiateSockets();

        // Try to connect sockets
        try
        {
            pull_socket.Connect(pose_host_address);
            push_socket.Connect(video_host_address);
            Debug.Log("Sockets bound.");
            // Save ip address for use on next boot.
            PlayerPrefs.SetString(client_ip_pref_key, inputIPString);
            PlayerPrefs.Save();
        }
        catch (Exception)
        {
            Debug.LogError("Input address from textbox is invalid. Note that hostnames are not supported!");
            throw;
        }

    }

    /* 
	 * Update is called once per frame
	 * Take the most recent ZMQ message and use it to position the cameras.
	 * If there has not been a recent message, the renderer should probably pause rendering until a new request is received. 
    */

    void Update()
    {
        if (pull_socket.HasIn || socket_initialized)
        {
            // Receive most recent message
            var msg = new NetMQMessage();
            var new_msg = new NetMQMessage();

            // Wait for a message from the client.
            bool received_new_packet = pull_socket.TryReceiveMultipartMessage(new TimeSpan(0, 0, connection_timeout_seconds), ref new_msg);

            if (!received_new_packet && socket_initialized)
            {
                // Close ZMQ sockets
                pull_socket.Close();
                push_socket.Close();
                Debug.Log("Terminated ZMQ sockets.");
                NetMQConfig.Cleanup();
                Thread.Sleep(100); // [ms]
                // Restart FlightGoggles and wait for a new connection.
                SceneManager.LoadScene(topLevelSceneName);
                // Kill this gameobject/controller script.
                Destroy(this.gameObject);
                // Don't bother with the rest of the script.
                return;

            }

            // Check if this is the latest message
            while (pull_socket.TryReceiveMultipartMessage(ref new_msg)) ;

            // Check that we got the whole message
            if (new_msg.FrameCount >= msg.FrameCount) { msg = new_msg; }

            if (msg.FrameCount != 2) { return; }

            if (msg[1].MessageSize < 10) { return; }

            // Get scene state from LCM
            state = JsonConvert.DeserializeObject<StateMessage_t>(msg[1].ConvertToString());

            // Make sure that all objects are initialized properly
            initializeObjects();
            // Ensure that dynamic object settings such as depth-scaling and color are set correctly.
            updateDynamicObjectSettings();
            // Update position of game objects.
            updateObjectPositions();

            // Do collision detection
            updateCameraCollisions();
            // Run landmark visibility checks
            updateLandmarkVisibility();
            // Compute sensor data
            updateLidarData();

            // Mark socket as initialized
            socket_initialized = true;

        
        } else
        {
            // Throttle to 10hz when idle
            Thread.Sleep(100); // [ms]
        }
    }


    /* ==================================
     * FlightGoggles High Level Functions 
     * ==================================
     */


    // Tries to initialize uninitialized objects multiple times until the object is initialized.
    // When everything is initialized, this function will NOP.
    void initializeObjects()
    {

        // Initialize Screen & keep track of frames to skip
        internal_state.screenSkipFrames = Math.Max(0, internal_state.screenSkipFrames - 1);

        // NOP if Unity wants us to skip this frame.
        if (internal_state.screenSkipFrames > 0){
            return;
        }
        
        // Run initialization steps in order.
        switch (internal_state.initializationStep)
        {
            // Load scene if needed.
            case 0:
                loadScene();
                internal_state.initializationStep++;
                // Takes one frame to take effect.
                internal_state.screenSkipFrames++;
                // Skip the rest of this frame
                break;
                
            // Initialize screen if scene is fully loaded and ready.
            case 1:
                resizeScreen();
                internal_state.initializationStep++;
                // Takes one frame to take effect.
                internal_state.screenSkipFrames++;
                // Skip the rest of this frame
                break;

            // Initialize gameobjects if screen is ready to render.
            case 2:
                instantiateObjects();
                instantiateCameras();
                internal_state.initializationStep++;
                // Takes one frame to take effect.
                internal_state.screenSkipFrames++;
                // Skip the rest of this frame
                break;

            // Ensure cameras are rendering to correct portion of GPU backbuffer.
            // Note, this should always be run after initializing the cameras.
            case 3:
                setCameraViewports();
                // Go to next step.
                internal_state.initializationStep++;
                // Takes one frame to take effect.
                internal_state.screenSkipFrames++;
                // Skip the rest of this frame
                break;

            case 4:
                setCameraPostProcessSettings();
                enableCollidersAndLandmarks();
                // Set initialization to -1 to indicate that we're done initializing.
                internal_state.initializationStep=-1;
                // Takes one frame to take effect.
                internal_state.screenSkipFrames++;
                // Skip the rest of this frame
                break;

            
            // If initializationStep does not match any of the ones above
            // then initialization is done and we need do nothing more.
                
        }
    }

    void setCameraPostProcessSettings()
    {

        state.cameras.ForEach(
            obj_state =>
            {
                // Get object
                ObjectState_t internal_object_state = internal_state.getWrapperObject(obj_state.ID, camera_template);
                GameObject obj = internal_object_state.gameObj;


                // Copy and save postProcessingProfile into internal_object_state.
                /*
                var postBehaviour = obj.GetComponent<PostProcessingBehaviour>();
                internal_object_state.postProcessingProfile = Instantiate(postBehaviour.profile);
                postBehaviour.profile = internal_object_state.postProcessingProfile;
                */

                /*
                // Enable depth if needed.
                if (obj_state.isDepth)
                {
                    
                    var debugSettings = internal_object_state.postProcessingProfile.debugViews.settings;

                    debugSettings.mode = BuiltinDebugViewsModel.Mode.Depth;
                    debugSettings.depth.scale = state.camDepthScale;
                    // Save the settings.
                    internal_object_state.postProcessingProfile.debugViews.settings = debugSettings;

                    // Disable AA
                    CTAA_PC AA = obj.GetComponent<CTAA_PC>();
                    // Check if AA object exists.
                    if (AA != null)
                    {
                        AA.enabled = false;
                    } else
                    {
                        // If CTAA is not installed on camera, fallback to PostProcessing FXAA.
                        internal_object_state.postProcessingProfile.antialiasing.enabled = false;
                    }
                    
            } else */

                /*
                {
                    // Set CTAA settings
                    CTAA_PC AA = obj.GetComponent<CTAA_PC>();
                    // Check if AA object exists.
                    if (AA != null)
                    {
                        AA.TemporalStability = state.temporalStability;
                        AA.HdrResponse = state.hdrResponse;
                        AA.Sharpness = state.sharpness;
                        AA.AdaptiveEnhance = state.adaptiveEnhance;
                        AA.TemporalJitterScale = state.temporalJitterScale;
                        AA.MicroShimmerReduction = state.microShimmerReduction;
                        AA.StaticStabilityPower = state.staticStabilityPower;
                        AA.enabled = true;
                    } else
                    {
                        // If CTAA is not installed on camera, fallback to PostProcessing FXAA.
                        AntialiasingModel.Settings AASettings = internal_object_state.postProcessingProfile.antialiasing.settings;

                        AASettings.method = AntialiasingModel.Method.Fxaa;
                        AASettings.fxaaSettings.preset = AntialiasingModel.FxaaPreset.ExtremeQuality;

                        // Save the settings.
                        internal_object_state.postProcessingProfile.antialiasing.settings = AASettings;
                        internal_object_state.postProcessingProfile.antialiasing.enabled = true;

                    }

                    // Ensure that Camera's RGB/Grayscale mode reflects the number of channels it has.
                    ColorGradingModel.Settings colorGradingSettings = internal_object_state.postProcessingProfile.colorGrading.settings;
                    float saturation = (obj_state.channels == 3) ? 1.0f : 0.0f;

                    colorGradingSettings.basic.saturation = saturation;
                    // Save the settings.
                    internal_object_state.postProcessingProfile.colorGrading.settings = colorGradingSettings;

                }*/
            });
        // 

    }

    // Update object and camera positions based on the positions sent by ZMQ.
    void updateObjectPositions()
    {
        if (internal_state.readyToRender){
            // Update camera positions

            internal_state.uavCenterOfMass = new Vector3();

            foreach (Camera_t obj_state in state.cameras)
            {
                // Get camera game object 
                GameObject obj = internal_state.getGameobject(obj_state.ID, camera_template);
                // Apply translation and rotation
                Vector3 pos = ListToVector3(obj_state.position);
                obj.transform.SetPositionAndRotation(pos, ListToQuaternion(obj_state.rotation));

                // Calculate center of mass
                internal_state.uavCenterOfMass += pos;

            }

            // Adjust camera colliders such that camera colliders are at the center of mass.
            internal_state.uavCenterOfMass /= state.numCameras;

            // Get camera to cast from
            ObjectState_t internal_object_state = internal_state.getWrapperObject(state.cameras[0].ID, camera_template);
            
            // Get camera collider
            SphereCollider cameraCollider = internal_object_state.gameObj.GetComponent<SphereCollider>();
            // Set collider global pos to center of mass.
            cameraCollider.center = internal_state.uavCenterOfMass - internal_object_state.gameObj.position;


            // Update Window positions
            foreach (Object_t obj_state in state.objects)
            {
                
                // Get game object 
                GameObject obj = internal_state.getGameobject(obj_state.ID, obj_state.prefabID);
                
                // Apply translation and rotation
                obj.transform.SetPositionAndRotation(ListToVector3(obj_state.position), ListToQuaternion(obj_state.rotation));

            }
        }
    }

    void updateDynamicObjectSettings()
    {
        // Update object settings.
        if (internal_state.readyToRender)
        {
            // Update depth cameras with dynamic depth scale.
            var depthCameras = state.cameras.Where(obj => obj.isDepth);
            foreach (var objState in depthCameras) {
                // Get object
                ObjectState_t internal_object_state = internal_state.getWrapperObject(objState.ID, camera_template);
                GameObject obj = internal_object_state.gameObj;

                /*
                // Scale depth.
                if (internal_object_state.postProcessingProfile.debugViews.settings.depth.scale != state.camDepthScale)
                {
                    var debugSettings = internal_object_state.postProcessingProfile.debugViews.settings;
                    debugSettings.mode = BuiltinDebugViewsModel.Mode.Depth;
                    debugSettings.depth.scale = state.camDepthScale;
                    internal_object_state.postProcessingProfile.debugViews.settings = debugSettings;
                }
                */

            }
        }

    }

    void updateCameraCollisions()
    {
        if (internal_state.readyToRender)
        {
            state.hasCameraCollision = false;
            var raycastingCameras = state.cameras.Where(obj => obj.hasCollisionCheck);
            foreach (var obj_state in raycastingCameras) {
                // Get object
                ObjectState_t internal_object_state = internal_state.getWrapperObject(obj_state.ID, camera_template);
                GameObject obj = internal_object_state.gameObj;
                // Check if object has collided (requires that collider hook has been setup on object previously.)
                state.hasCameraCollision = (state.hasCameraCollision || obj.GetComponent<collisionHandler>().hasCollided);
            }
        }
    }

    void updateLandmarkVisibility()
    {
        if (internal_state.readyToRender)
        {

            // Erase old set of landmarks
            state.landmarksInView = new List<Landmark_t>();

            // Get camera to cast from
            ObjectState_t internal_object_state = internal_state.getWrapperObject(state.cameras[0].ID, camera_template);
            Camera castingCamera = internal_object_state.gameObj.GetComponent<Camera>();
            Vector3 cameraPosition = internal_object_state.gameObj.transform.position;

            // Get camera collider
            Collider cameraCollider = internal_object_state.gameObj.GetComponent<Collider>();

            // Cull landmarks based on camera frustrum
            // Gives lookup table of screen positions.
            Dictionary<string, Vector3> visibleLandmarkScreenPositions = new Dictionary<string, Vector3>();

            foreach (KeyValuePair<string, GameObject> entry in internal_state.landmarkObjects)
            {
                Vector3 screenPoint = castingCamera.WorldToViewportPoint(entry.Value.transform.position);
                bool visible = screenPoint.z > 0 && screenPoint.x > 0 && screenPoint.x < 1 && screenPoint.y > 0 && screenPoint.y < 1;
                if (visible)
                {
                    visibleLandmarkScreenPositions.Add(entry.Key, screenPoint);
                }
            }


            int numLandmarksInView = visibleLandmarkScreenPositions.Count();

            // Batch raytrace from landmarks to camera
            var results = new NativeArray<RaycastHit>(numLandmarksInView, Allocator.TempJob);
            var commands = new NativeArray<RaycastCommand>(numLandmarksInView, Allocator.TempJob);

            int i = 0;
            var visibleLandmarkScreenPosList = visibleLandmarkScreenPositions.ToArray();
            foreach (var elm in visibleLandmarkScreenPosList)
            {
                var landmark = internal_state.landmarkObjects[elm.Key];
                Vector3 origin = landmark.transform.position;
                Vector3 direction = cameraPosition - origin;

                commands[i] = new RaycastCommand(origin, direction, distance: direction.magnitude, maxHits: max_num_ray_collisions);
                i++;
            }

            // Run the raytrace commands
            JobHandle handle = RaycastCommand.ScheduleBatch(commands, results, 1, default(JobHandle));
            // Wait for the batch processing job to complete.
            // @TODO: Move to end of frame.
            handle.Complete();

            // Cull based on number of collisions
            // Remove if it collided with something
            for (int j = 0; j < numLandmarksInView; j++)
            {
                // Check collisions. NOTE: indexing is via N*max_hits with first null being end of hit list.
                RaycastHit batchedHit = results[j];
                if (batchedHit.collider == null)
                {
                    // No collisions here. Add it to the current state.
                    var landmark = visibleLandmarkScreenPosList[j];
                    Landmark_t landmarkScreenPosObject = new Landmark_t();

                    landmarkScreenPosObject.ID = landmark.Key;
                    landmarkScreenPosObject.position = Vector3ToList(landmark.Value);
                    state.landmarksInView.Add(landmarkScreenPosObject);

                } else if (batchedHit.collider == cameraCollider)
                {
                    // No collisions here. Add it to the current state.
                    var landmark = visibleLandmarkScreenPosList[j];
                    Landmark_t landmarkScreenPosObject = new Landmark_t();

                    landmarkScreenPosObject.ID = landmark.Key;
                    landmarkScreenPosObject.position = Vector3ToList(landmark.Value);
                    state.landmarksInView.Add(landmarkScreenPosObject);
                }
                
            }

            results.Dispose();
            commands.Dispose();
        }
    }


    // Update Lidar data
    void updateLidarData()
    {
        if (internal_state.readyToRender)
        {
            // Find direction and origin of raytrace for LIDAR. 
            // In Unity, all objects are Y-UP. Therefore, a downward pointing lidar will be pointing along -Y in camera frame.
            Vector3 raycastDirection =  ListToQuaternion(state.cameras[0].rotation) * new Vector3(0, -1, 0);

            // Print direction for debug
            // Debug.DrawRay(internal_state.uavCenterOfMass, raycastDirection);

            // Run the raytrace
            RaycastHit lidarHit;
            float maxLidarDistance = 20;
            bool hasHit = Physics.Raycast(internal_state.uavCenterOfMass, raycastDirection, out lidarHit, maxLidarDistance);

            // Get distance. Return max+1 if out of range
            float raycastDistance = hasHit ? lidarHit.distance : maxLidarDistance+1;

            // Save the result of the raycast.
            state.lidarReturn = raycastDistance;
            // Debug.DrawLine(internal_state.uavCenterOfMass, internal_state.uavCenterOfMass + raycastDirection * raycastDistance);
        }
    }

    /* =============================================
     * FlightGoggles Initialization Functions 
     * =============================================
     */

    void loadScene(){
        // Load a scene, either from internal selection or external .obj or .dae file.
        if (state.sceneIsInternal || state.sceneIsDefault){
            // Load scene from internal scene selection
            // Get the scene name.
            string sceneName = (state.sceneIsDefault)? defaultSceneName : state.sceneFilename;
            Debug.Log("Loading scene: " + sceneName);

            // Make sure that splashscreen is disabled
            // splashScreen.SetActive(false);
            // Load the scene. 
            SceneManager.LoadScene(sceneName);

            // Load external scene
        } else {
            // Throw error if trilib does not exist
#if TRILIB_DOES_NOT_EXIST
            throw new System.InvalidOperationException("Cannot import external 3D models without including TriLib in the project directory. Please read the FlightGoggles README for more information.");
#else
            // Load default lighting scene.
            //SceneManager.LoadScene(defaultLightingScene, LoadSceneMode.Additive);
            // Make new empty scene for holding the .obj data.
            Scene externallyLoadedScene = SceneManager.CreateScene("Externally_Loaded_Scene");
            // Tell Unity to put new objects into the newly created container scene.
            SceneManager.SetActiveScene(externallyLoadedScene);

            // Load in new scene model using TriLib
            using (var assetLoader = new AssetLoader())
            { // Initializes our Asset Loader.
                // Creates an Asset Loader Options object.
                var assetLoaderOptions = ScriptableObject.CreateInstance<AssetLoaderOptions>();
                // Specify loading options.
                assetLoaderOptions.DontLoadAnimations = false;
                assetLoaderOptions.DontLoadCameras = true;
                assetLoaderOptions.DontLoadLights = false;
                assetLoaderOptions.DontLoadMaterials = false;
                assetLoaderOptions.AutoPlayAnimations = true;
                // Loads scene model into container scene.
                assetLoader.LoadFromFile(state.sceneFilename, assetLoaderOptions);
            }
            // Set our loaded scene as static
            // @TODO
            // StaticBatchingUtility.Combine(externallyLoadedScene);
#endif

        }
    }

    void resizeScreen(){
        // Set the max framerate to something very high
        Application.targetFrameRate = 100000000;
        // initialize the display to a window that fits all cameras
        Screen.SetResolution(state.screenWidth, state.screenHeight, false);
        // Set render texture to the correct size
        rendered_frame = new Texture2D(state.screenWidth, state.screenHeight, TextureFormat.RGB24, false, true);
    }

    void enableCollidersAndLandmarks(){
        internal_state.landmarkObjects = new Dictionary<string, GameObject>();

        // Enable object colliders in scene
        foreach(Collider c in FindObjectsOfType<Collider>())
        {
            c.enabled = true;
        }

        // Disable unneeded camera colliders
        var nonRaycastingCameras = state.cameras.Where(obj => !obj.hasCollisionCheck);
        foreach (Camera_t obj_state in nonRaycastingCameras){

            ObjectState_t internal_object_state = internal_state.getWrapperObject(obj_state.ID, camera_template);

            // Get camera collider
            Collider cameraCollider = internal_object_state.gameObj.GetComponent<Collider>();
            cameraCollider.enabled = false;
        }

        // Find all landmarks in scene
        foreach (GameObject obj in GameObject.FindGameObjectsWithTag("IR_Markers")){
            // Tag the landmarks
            string gateName = obj.transform.parent.parent.name;
            string landmarkID = gateName + "_" + obj.name;
            // Save for later use
            try
            {
                internal_state.landmarkObjects.Add(landmarkID, obj);
            } catch (ArgumentException)
            {
                Debug.LogWarning("Landmark ID " + landmarkID + " already exists!");
            }
        }

        //UNload unused assets
        Resources.UnloadUnusedAssets();



    }

    void instantiateObjects(){
        // Initialize additional objects
        state.objects.ForEach(
            obj_state =>
            {
                // Get object
                ObjectState_t internal_object_state = internal_state.getWrapperObject(obj_state.ID, obj_state.prefabID);
                GameObject obj = internal_object_state.gameObj;
                // @TODO Set object size
                //obj.transform.localScale = ListToVector3(obj_state.size);
            }
        );

        // Check if should load obstacle perturbation file.
        if (obstacle_perturbation_file.Length > 0) {
            using (var reader = new StreamReader(obstacle_perturbation_file)) {
                while (reader.Peek() >= 0) {
                    // Read line
                    string str;
                    str = reader.ReadLine();

                    // Parse line
                    string objectName = str.Split(':')[0];
                    string translationString = str.Split(':')[1];
                    float[] translationsFloat = Array.ConvertAll(translationString.Split(','), float.Parse);

                    // Find object
                    GameObject obj = GameObject.Find(objectName);
                    if (obj != null)
                    {
                        //// Check if object is statically batched. (NOT AVAILABLE IN STANDALONE BUILD)
                        //int flags = (int)GameObjectUtility.GetStaticEditorFlags(obj);
                        //if ((flags & 4)!= 0)
                        //{
                        //    // Gameobject is not movable!!!
                        //    Debug.LogError("WARNING: " + objectName + " is statically batched and not movable! Make sure the gameobject is only lightmap static.");
                        //} else
                        //{
                            // Translate and rotate object
                            obj.transform.Translate(-translationsFloat[1], 0, translationsFloat[0], Space.World);
                            obj.transform.Rotate(0, translationsFloat[2], 0, Space.World);

                        //}



                    }
                }

            }

        }
        if (outputLandmarkLocations)
        {
            // Output current locations.
            Dictionary<string, List<GameObject>> GateMarkers = new Dictionary<string, List<GameObject>>();

            // Find all landmarks and print to file.
            foreach (GameObject obj in GameObject.FindGameObjectsWithTag("IR_Markers"))
            {
                // Tag the landmarks
                string gateName = obj.transform.parent.parent.name;
                string landmarkID = obj.name;

                // Check if gate already exists.
                if (GateMarkers.ContainsKey(gateName))
                {
                    GateMarkers[gateName].Add(obj);
                }
                else
                {
                    List<GameObject> markerList = new List<GameObject>();
                    markerList.Add(obj);
                    GateMarkers.Add(gateName, markerList);
                }
            }

            // Print results
            //Write some text to the test.txt file
            StreamWriter writer = new StreamWriter("markerLocations.yaml", false);
            foreach (var pair in GateMarkers)
            {
                writer.WriteLine(pair.Key + ":");
                writer.Write("  location: [");

                


                int i = 0;
                // Sort landmark IDs
                foreach (GameObject marker in pair.Value.OrderBy(gobj=>gobj.name).ToList())
                {

                    // Convert vector from EUN to NWU.
                    Vector3 NWU = new Vector3(marker.transform.position.z, -marker.transform.position.x, marker.transform.position.y);

                    // Print out locations in yaml format.

                    writer.Write("[" + NWU.x + ", " + NWU.y + ", " + NWU.z + "]");
                    if (i < 3)
                    {
                        writer.Write(", ");
                    }
                    else
                    {
                        writer.WriteLine("]");
                    }
                    i++;
                }

            }
            writer.Close();
        }
        
    }

    void instantiateCameras(){
        // Disable cameras in scene
        foreach (Camera c in FindObjectsOfType<Camera>())
        {
            c.enabled = false;
        }

        // Initialize new Camera objects.
        state.cameras.ForEach(
            obj_state =>
            {
                // Get object
                ObjectState_t internal_object_state = internal_state.getWrapperObject(obj_state.ID, camera_template);
                GameObject obj = internal_object_state.gameObj;
                // Ensure FOV is set for camera.
                obj.GetComponent<Camera>().fieldOfView = state.camFOV;

                // Apply translation and rotation
                obj.transform.SetPositionAndRotation(ListToVector3(obj_state.position), ListToQuaternion(obj_state.rotation));
            }
        );

    }

    void setCameraViewports(){
        // Resize camera viewports.
        state.cameras.ForEach(
            obj_state =>
            {
                // Get object
                ObjectState_t internal_object_state = internal_state.getWrapperObject(obj_state.ID, camera_template);
                GameObject obj = internal_object_state.gameObj;
                // Make sure camera renders to the correct portion of the screen.
                obj.GetComponent<Camera>().pixelRect = new Rect(0, state.camHeight * (state.numCameras - obj_state.outputIndex - 1), state.camWidth, state.camHeight);
                // enable Camera.
                obj.SetActive(true);
            }
        );
    }


    /* ==================================
     * FlightGoggles Low Level Functions 
     * ==================================
     */

    // Old version with managed memory
    // Cut image block for an individual camera from larger provided image.
    //public byte[] get_raw_image(Camera_t cam, byte[] raw, int numCams)
    //{


    //    int num_bytes_to_copy = cam.channels * state.camWidth * state.camHeight;
    //    //Debug.Log(cam.channels);
    //    //int num_bytes_to_copy = 3 * state.camWidth * state.camHeight;

    //    byte[] output = new byte[num_bytes_to_copy];

    //    // Reverse camera indexing since the camera output is globally flipped on the Y axis.
    //    int outputIndex = numCams - 1 - cam.outputIndex;

    //    // Figure out where camera data starts and ends
    //    int y_start = outputIndex * state.camHeight;
    //    int y_end = (outputIndex+1) * state.camHeight;
        
    //    // Calculate start and end byte
    //    int byte_start = (y_start * state.screenWidth) * 3;
    //    int byte_end = (y_end * state.screenWidth) * 3;

        
    //    // Create a copy of the array
    //    int px_stride = 4 - cam.channels;
    //    for (int i = 0; i < num_bytes_to_copy; i++)
    //    {
    //        output[i] = raw[byte_start + i*px_stride];

    //    }
        

    //    return output;
    //}

    //public struct ConvertRGBAToRGB : IJobParallelFor
    //{
    //    [ReadOnly]
    //    public NativeSlice<byte> input;
    //    [WriteOnly]
    //    public NativeArray<byte> output;
    //    // Params
    //    [ReadOnly] 
    //    public int px_stride;

    //    // i is the iteration loop index
    //    // In this case, it is the index of the output byte.
    //    public void Execute( int i)
    //    {
    //        output[i] = input[i * px_stride];
    //    }
    //}

    
    // Cut image block for an individual camera from larger provided image.
    //public byte[] get_raw_image(Camera_t cam, NativeArray<byte> raw, int numCams)
    //{



    //    // int num_bytes_to_copy = cam.channels * state.camWidth * state.camHeight;
        
    //    //Debug.Log(cam.channels);
    //    //int num_bytes_to_copy = 3 * state.camWidth * state.camHeight;

    //    byte[] output = new byte[num_bytes_to_copy];

    //    // Reverse camera indexing since the camera output is globally flipped on the Y axis.
    //    int outputIndex = numCams - 1 - cam.outputIndex;

    //    // Figure out where camera data starts and ends
    //    int y_start = outputIndex * state.camHeight;
    //    int y_end = (outputIndex + 1) * state.camHeight;

    //    // Calculate start and end byte
    //    int byte_start = (y_start * state.screenWidth) * 3;
    //    int byte_end = (y_end * state.screenWidth) * 3;


    //    // Create a copy of the array
    //    int px_stride = 4 - cam.channels;
    //    for (int i = 0; i < num_bytes_to_copy; i++)
    //    {
    //        output[i] = raw[byte_start + i * px_stride];

    //    }


    //    return output;
    //}



    // Reads a scene frame from the GPU backbuffer and sends it via ZMQ.
    void sendFrameOnWire()
    {
        // Read pixels from screen backbuffer (expensive).
        rendered_frame.ReadPixels(new Rect(0, 0, state.screenWidth, state.screenHeight), 0, 0);
        rendered_frame.Apply(); // Might not actually be needed since only applies setpixel changes.

        byte[] raw = rendered_frame.GetRawTextureData();
        
        // Get metadata
        RenderMetadata_t metadata = new RenderMetadata_t(state);

        // Create packet metadata
        var msg = new NetMQMessage();
        msg.Append(JsonConvert.SerializeObject(metadata));

        // Process each camera's image, compress the image, and serialize the result.
        // Compression is disabled for now...
        // Stride is also disabled for now. Outputs RGB blocks with stride 3.

        state.cameras.ForEach(cam => {
            // Length of RGB slice
            int rgbImageLength = 3 * state.camWidth * state.camHeight;

            // Reverse camera indexing since the camera output is globally flipped on the Y axis.
            int outputIndex = state.numCameras - 1 - cam.outputIndex;

            // Figure out where camera data starts and ends
            int yStart = outputIndex * state.camHeight;
            int yEnd = (outputIndex + 1) * state.camHeight;

            // Calculate start and end byte
            int byteStart = (yStart * state.screenWidth) * 3;
            //int byte_end = (y_end * state.screenWidth) * 3;

            // Get view of RGB camera image
            //NativeSlice<byte> rgbImage = raw.Slice<byte>(byteStart, rgbImageLength);



            //Check if need to convert RGB image to grayscale @TODO
            //if (cam.channels == 1)
            //{
            //    // Get single channels
            //    NativeSlice<byte> redArray = rgbImage.SliceWithStride<byte>(0);
            //    NativeSlice<byte> greenArray = rgbImage.SliceWithStride<byte>(1);
            //    NativeSlice<byte> blueArray = rgbImage.SliceWithStride<byte>(2);
            //    // Get output array
            //    NativeArray<byte> grayscaleArray = new NativeArray<byte>(rgbImageLength / 3, Allocator.TempJob);
            //    // Spawn conversion job
            //    // @ TODO
            //    convertToGrayscale(m_NativeRed, m_NativeGreen, m_NativeBlue, ref grayscaleConversionBurstJobHandle);
            //}

            // Append image to message
            //byte[] imageData = rgbImage.ToArray();
            //rgbImage.CopyTo(imageData);

            byte[] imageData = new byte[rgbImageLength];
            Array.Copy(raw, byteStart, imageData, 0, rgbImageLength);


            //fixed (byte* pSource = &raw[0])
            //{
            //    var frame = new NetMQFrame(*(byte*)pSource, rgbImageLength);
            //}
            
            msg.Append(imageData);
            });

        if (push_socket.HasOut)
        {
            push_socket.TrySendMultipartMessage(msg);
        }
           

    }

    /* ==================================
     * FlightGoggles Helper Functions 
     * ==================================
     */

    // Helper function for getting command line arguments
    private static string GetArg(string name, string default_return)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && args.Length > i + 1)
            {
                return args[i + 1];
            }
        }
        return default_return;
    }

    // Helper functions for converting list -> vector
    public static Vector3 ListToVector3(IList<float> list) { return new Vector3(list[0], list[1], list[2]); }
    public static Quaternion ListToQuaternion(IList<float> list) { return new Quaternion(list[0], list[1], list[2], list[3]); }
    public static Color ListHSVToColor(IList<float> list) { return Color.HSVToRGB(list[0], list[1], list[2]); }

    // Helper functions for converting  vector -> list
    public static List<float> Vector3ToList(Vector3 vec) { return new List<float>(new float[] { vec[0], vec[1], vec[2] }); }

}
