using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Barracuda;

using System.IO;
using TFClassify;
using System.Linq;
using System.Collections;

// TODO: 
// Move the anchored objects in a separate list, used by the AnchorCreators. 
// Otherwise the anchors are resetted at each update, because of the
// boxSavedOutlines.Clear(),
// Do not place visualization at click, bu use a placeholder, and use a 
// panel to define the rule element. After the element is saved, the 
// placeholder is updated with these new info, or discarded.
// maybe: use a dictionary instead of lists for store saved bounding boxes

public class PhoneARCamera : MonoBehaviour
{
    [SerializeField]
    ARCameraManager m_CameraManager;
    
    private Dictionary<string, BoundingBox> _detectableDict = new Dictionary<string, BoundingBox>();
    
    public Dictionary<string, BoundingBox> detectableDict
    {
        get { return _detectableDict; }
        set { _detectableDict= value; }
    }

        
        // Cache ARRaycastManager GameObject from ARCoreSession
    private ARRaycastManager _raycastManager;

    // List for raycast hits is re-used by raycast manager
    private static readonly List<ARRaycastHit> Hits = new List<ARRaycastHit>();

    /// <summary>
    /// Get or set the <c>ARCameraManager</c>.
    /// </summary>
    public ARCameraManager cameraManager
    {
        get => m_CameraManager;
        set => m_CameraManager = value;
    }

    [SerializeField]
    RawImage m_RawImage;

    /// <summary>
    /// The UI RawImage used to display the image on screen. (deprecated)
    /// </summary>
    public RawImage rawImage
    {
        get { return m_RawImage; }
        set { m_RawImage = value; }
    }

    public enum Detectors
    {
        Yolo2_tiny,
        Yolo3_tiny
    };
    public Detectors selected_detector;

    public Detector detector = null;

    public float shiftX = 0f;
    public float shiftY = 0f;
    public float scaleFactor = 1;

    public string test;

    public Color colorTag = new Color(0.3843137f, 0, 0.9333333f);
    private static GUIStyle labelStyle;
    private static Texture2D boxOutlineTexture;
    // bounding boxes detected for current frame
    private IList<BoundingBox> boxOutlines;
    // bounding boxes detected across frames
    public List<BoundingBox> boxSavedOutlines = new List<BoundingBox>();
    // lock model when its inferencing a frame
    private bool isDetecting = false;
    
    // labels of element permanently added to anchors 
    public List<string> permanentlyStoredLabels = new List<string>();

    // list of labels detected across frames
    public List<String> saveLabels = new List<String>();

    // the number of frames that bounding boxes stay static
    private int staticNum = 0;
    public bool localization = false;

    // util variables to store the number of detections
    public int detectionCount = 0; 
    public int detectionLast = 0; 
    
    Texture2D m_Texture;

    // check if ..
    public bool checkIfLabelInList(string label)
    {
        if (!saveLabels.Contains(label))
        {
            ScreenLog.Log("list does not contains " + label + ", added");
            return false;
        }
        //ScreenLog.Log("list already contains " + label + "!!!");
        return true;
    }
    

    void OnEnable()
    {
        if (m_CameraManager != null)
        {
            m_CameraManager.frameReceived += OnCameraFrameReceived;
        }

        boxOutlineTexture = new Texture2D(1, 1);
        boxOutlineTexture.SetPixel(0, 0, this.colorTag);
        boxOutlineTexture.Apply();
        labelStyle = new GUIStyle();
        labelStyle.fontSize = 50;
        labelStyle.normal.textColor = this.colorTag;

        if (selected_detector == Detectors.Yolo2_tiny)
        {
            detector = GameObject.Find("Detector Yolo2-tiny").GetComponent<DetectorYolo2>();
        }
        else if (selected_detector == Detectors.Yolo3_tiny)
        {
            detector = GameObject.Find("Detector Yolo3-tiny").GetComponent<DetectorYolo3>();
        }
        else
        {
            ScreenLog.Log("DEBUG: Invalid detector model");
        }

        this.detector.Start();

        CalculateShift(this.detector.IMAGE_SIZE);
    }

    public void awake()
    {
        detectableDict.Add("estimote_beacon", null);
        detectableDict.Add("Lamp", null);
        detectableDict.Add("Window", null);
        detectableDict.Add("philips_hue_sensor", null);
        detectableDict.Add("echo_dot", null);
        detectableDict.Add("honeywell_smoke_gas", null);
    }

    void OnDisable()
    {
        if (m_CameraManager != null)
        {
            m_CameraManager.frameReceived -= OnCameraFrameReceived;
        }
    }

    public void OnRefresh()
    {
        //ScreenLog.Log("DEBUG: onRefresh, removing anchors and boundingboxes");
        localization = false;
        staticNum = 0;
        // clear bounding box containers
        boxSavedOutlines.Clear();
        boxOutlines.Clear();
        // clear anchor
        AnchorCreator anchorCreator = FindObjectOfType<AnchorCreator>();
        anchorCreator.RemoveAllAnchors();
    }
        
   
        unsafe void OnCameraFrameReceived(ARCameraFrameEventArgs eventArgs)
    {
        // Attempt to get the latest camera image. If this method succeeds,
        // it acquires a native resource that must be disposed (see below).
        XRCpuImage image;
        if (!cameraManager.TryAcquireLatestCpuImage(out image))
        {
            return;
        }

        // Once we have a valid XRCameraImage, we can access the individual image "planes"
        // (the separate channels in the image). XRCameraImage.GetPlane provides
        // low-overhead access to this data. This could then be passed to a
        // computer vision algorithm. Here, we will convert the camera image
        // to an RGBA texture (and draw it on the screen).

        // Choose an RGBA format.
        // See XRCameraImage.FormatSupported for a complete list of supported formats.
        var format = TextureFormat.RGBA32;

        if (m_Texture == null || m_Texture.width != image.width || m_Texture.height != image.height)
        {
            m_Texture = new Texture2D(image.width, image.height, format, false);
        }

        // Convert the image to format, flipping the image across the Y axis.
        // We can also get a sub rectangle, but we'll get the full image here.
        var conversionParams = new XRCpuImage.ConversionParams(image, format, XRCpuImage.Transformation.None);

        // Texture2D allows us write directly to the raw texture data
        // This allows us to do the conversion in-place without making any copies.
        var rawTextureData = m_Texture.GetRawTextureData<byte>();
        try
        {
            image.Convert(conversionParams, new IntPtr(rawTextureData.GetUnsafePtr()), rawTextureData.Length);
        }
        finally
        {
            // We must dispose of the XRCameraImage after we're finished
            // with it to avoid leaking native resources.
            image.Dispose();
        }

        // Apply the updated texture data to our texture
        m_Texture.Apply();

        // If bounding boxes are static for certain frames, start localization
        if (staticNum == 50)
        {
            //ScreenLog.Log("Bounding box static for 50 frames");
            //localization = true;
        }
        else
        {
            // detect object and create current frame outlines
            TFDetect();
            // merging outliens across frames
            //GroupBoxOutlines();
            NewBoxOutliner();

        }
        // Set the RawImage's texture so we can visualize it.
        m_RawImage.texture = m_Texture;

    }


    public void OnGUI()
    {
        // Do not draw bounding boxes after localization.
        if (localization)
        {
            return;
        }

        if (this.boxSavedOutlines != null && this.boxSavedOutlines.Any())
        {
            foreach (var outline in this.boxSavedOutlines)
            {
                if (!checkIfLabelInList(outline.Label))
                {
                    this.saveLabels.Add(outline.Label);
                    ScreenLog.Log(saveLabels.Count.ToString());
                }
                DrawBoxOutline(outline, scaleFactor, shiftX, shiftY);
            }
        }
    }


    // Check if the newOutline has higher confidence of at last 1 saved label
    // TODO: it is useless to use a list if it is planned to only store 1 
    // class label at time. See which struct to eventually use. 
    private bool checkHigherConfidence(BoundingBox newOutline)
    {
        foreach (var outline in this.boxSavedOutlines)
        {
            if(newOutline.Label == outline.Label)
            {
                if(newOutline.Confidence >= outline.Confidence)
                {
                    return true;
                }
            }
        }
        return false;
    }
    
    //
    private void removeItemsByLabel(string label)
    {
        List<BoundingBox> itemsToRemove = new List<BoundingBox>();
        foreach (var outline in this.boxSavedOutlines)
        {
            if(label == outline.Label)
            {
                //ScreenLog.Log("FOUND ELEMENT WITH SAME LABEL IN SAVED LIST: REMOVING " + outline.Label);
                itemsToRemove.Add(outline);
            }
        }
        this.boxSavedOutlines.RemoveAll(item => itemsToRemove.Contains(item));
    }
    
    //
    private bool alreadyInToSaveList(BoundingBox element, List<BoundingBox> elementsToAdd)
    {
        foreach (var outline in elementsToAdd)
        {
            if (element.Label == outline.Label)
            {
                return true;
            }
        }
        return false;
    }
    
    // Remove items if they are not on the detection list and the camera moved 
    // more than X pixels (TODO)
    /*
    private void removeNoMoreVisibleItems(List<BoundingBox>boundingBoxList)
    {
        List<BoundingBox> itemsToRemove= new List<BoundingBox>();
        foreach (var elementSaved in this.boxSavedOutlines)
        {
            bool found = false;
            foreach (var elementDetected in boundingBoxList)
            {
                if (elementSaved.Label == elementDetected.Label)
                {
                    found = true;
                }
            }
            if (!found)
            {
                itemsToRemove.Add(elementSaved);
            }
        }
        //remove: need a "camera stability" check before
        this.boxSavedOutlines.RemoveAll(item => itemsToRemove.Contains(item));
    }
    */
    
    // Save the detected bounding boxes. Overlapping ones are discarded. 
    private void NewBoxOutliner()
    {
        List<BoundingBox> itemsToAdd = new List<BoundingBox>();
        List<String> alreadyFound = new List<String>();
        // Remove all old detections. It is not worthy to make confidence check, 
        // because the phone is not static, hence old detections will always
        // move away from the target object. 
        // Eventually, test if the phone is standing still.
        if (localization)
        {
            return;
        }
        boxSavedOutlines.Clear();
        foreach (var outline in this.boxOutlines)
        {
            if (!alreadyInToSaveList(outline, itemsToAdd))
            {
                itemsToAdd.Add(outline);
            }
            //else
            //{
                // If their bounding box overlaps, keeps the higher confidence one
                // ... 
                // Else, keep both
                // ...
            //}
        }
        this.boxSavedOutlines.AddRange(itemsToAdd);
    }

    // merging bounding boxes and save result to boxSavedOutlines
    private void GroupBoxOutlines()
    {
        /*
        // if savedoutlines is empty, add current frame outlines if possible.
        if (this.boxSavedOutlines.Count == 0)
        {
            // no bounding boxes in current frame
            if (this.boxOutlines == null || this.boxOutlines.Count == 0)
            {
                return;
            }
            // deep copy current frame bounding boxes
            foreach (var outline in this.boxOutlines)
            {
                this.boxSavedOutlines.Add(outline);
            }
            return;
        }
        */

        // adding current frame outlines to existing savedOulines and merge if possible.
        bool addOutline = false;
        foreach (var outline1 in this.boxOutlines)
        {
            bool unique = true;
            List<BoundingBox> itemsToAdd = new List<BoundingBox>();
            List<BoundingBox> itemsToRemove = new List<BoundingBox>();
            foreach (var outline2 in this.boxSavedOutlines)
            {
                // get the class of the 2 objects 
                // if they are the same, use high confidence one
                //IsSameClass();
                // if two bounding boxes are for the same object, use high confidnece one
                if (IsSameObject(outline1, outline2))
                //if (outline1.Label == outline2.Label)
                {
                    unique = false;
                    if (outline1.Confidence > outline2.Confidence + 0.05F) //& outline2.Confidence < 0.5F)
                    {
                        //ScreenLog.Log("DEBUG: add detected boxes in this frame.");
                        //ScreenLog.Log($"DEBUG: Add Label: {outline1.Label}. Confidence: {outline1.Confidence}.");
                        //ScreenLog.Log($"DEBUG: Remove Label: {outline2.Label}. Confidence: {outline2.Confidence}.");
                        ScreenLog.Log($"DEBUG: found 2 Label of the same object, removing the less confident");

                        itemsToRemove.Add(outline2);
                        itemsToAdd.Add(outline1);
                        addOutline = true;
                        staticNum = 0;
                        break;
                    }
                }
            }
            this.boxSavedOutlines.RemoveAll(item => itemsToRemove.Contains(item));
            this.boxSavedOutlines.AddRange(itemsToAdd);

            // if outline1 in current frame is unique, add it permanently
            if (unique) //?????????
            {
                ScreenLog.Log($"DEBUG: add detected boxes in this frame");
                addOutline = true;
                staticNum = 0;
                this.boxSavedOutlines.Add(outline1);
                ScreenLog.Log($"Add Label: {outline1.Label}. Confidence: {outline1.Confidence}.");
            }
        }
        if (!addOutline)
        {
            staticNum += 1;
        }

        // merge same bounding boxes
        // remove will cause duplicated bounding box?
        List<BoundingBox> temp = new List<BoundingBox>();
        foreach (var outline1 in this.boxSavedOutlines)
        {
            if (temp.Count == 0)
            {
                temp.Add(outline1);
                continue;
            }

            List<BoundingBox> itemsToAdd = new List<BoundingBox>();
            List<BoundingBox> itemsToRemove = new List<BoundingBox>();
            foreach (var outline2 in temp)
            {
                //ScreenLog.Log(this);
                //if (IsSameObject(outline1, outline2))
                if (outline1.Label == outline2.Label)
                {
                    if (outline1.Confidence > outline2.Confidence)
                    {
                        itemsToRemove.Add(outline2);
                        itemsToAdd.Add(outline1);
                        ScreenLog.Log("DEBUG: merge bounding box conflict!!!");
                    }
                }
                else
                {
                    itemsToAdd.Add(outline1);
                }
            }
            temp.RemoveAll(item => itemsToRemove.Contains(item));
            temp.AddRange(itemsToAdd);
        }
        this.boxSavedOutlines = temp;
    }

    private bool IsSameClass()
    {
        return true;
    }

    // For two bounding boxes, if at least one center is inside the other box,
    // treate them as the same object.
    private bool IsSameObject(BoundingBox outline1, BoundingBox outline2)
    {
        var xMin1 = outline1.Dimensions.X * this.scaleFactor + this.shiftX;
        var width1 = outline1.Dimensions.Width * this.scaleFactor;
        var yMin1 = outline1.Dimensions.Y * this.scaleFactor + this.shiftY;
        var height1 = outline1.Dimensions.Height * this.scaleFactor;
        float center_x1 = xMin1 + width1 / 2f;
        float center_y1 = yMin1 + height1 / 2f;

        var xMin2 = outline2.Dimensions.X * this.scaleFactor + this.shiftX;
        var width2 = outline2.Dimensions.Width * this.scaleFactor;
        var yMin2 = outline2.Dimensions.Y * this.scaleFactor + this.shiftY;
        var height2 = outline2.Dimensions.Height * this.scaleFactor;
        float center_x2 = xMin2 + width2 / 2f;
        float center_y2 = yMin2 + height2 / 2f;

        bool cover_x = (xMin2 < center_x1) && (center_x1 < (xMin2 + width2));
        bool cover_y = (yMin2 < center_y1) && (center_y1 < (yMin2 + height2));
        bool contain_x = (xMin1 < center_x2) && (center_x2 < (xMin1 + width1));
        bool contain_y = (yMin1 < center_y2) && (center_y2 < (yMin1 + height1));

        return (cover_x && cover_y) || (contain_x && contain_y);
    }

    private void CalculateShift(int inputSize)
    {
        int smallest;

        if (Screen.width < Screen.height)
        {
            smallest = Screen.width;
            this.shiftY = (Screen.height - smallest) / 2f;
        }
        else
        {
            smallest = Screen.height;
            this.shiftX = (Screen.width - smallest) / 2f;
        }

        this.scaleFactor = smallest / (float)inputSize;
    }

    private void TFDetect()
    {
        if (this.isDetecting)
        {
            return;
        }

        this.isDetecting = true;
        StartCoroutine(ProcessImage(this.detector.IMAGE_SIZE, result =>
        {
            StartCoroutine(this.detector.Detect(result, boxes =>
            {
                this.boxOutlines = boxes;
                Resources.UnloadUnusedAssets();
                this.isDetecting = false;
            }));
        }));
    }


    private IEnumerator ProcessImage(int inputSize, System.Action<Color32[]> callback)
    {
        Coroutine croped = StartCoroutine(TextureTools.CropSquare(m_Texture,
           TextureTools.RectOptions.Center, snap =>
           {
               var scaled = Scale(snap, inputSize);
               var rotated = Rotate(scaled.GetPixels32(), scaled.width, scaled.height);
               callback(rotated);
           }));
        yield return croped;
    }


    private void DrawBoxOutline(BoundingBox outline, float scaleFactor, float shiftX, float shiftY)
    {
        var x = outline.Dimensions.X * scaleFactor + shiftX;
        var width = outline.Dimensions.Width * scaleFactor;
        var y = outline.Dimensions.Y * scaleFactor + shiftY;
        var height = outline.Dimensions.Height * scaleFactor;

        DrawRectangle(new Rect(x, y, width, height), 10, this.colorTag);
        DrawLabel(new Rect(x, y - 80, 200, 20), $"Localizing {outline.Label}: {(int)(outline.Confidence * 100)}%");
    }


    public static void DrawRectangle(Rect area, int frameWidth, Color color)
    {
        Rect lineArea = area;
        lineArea.height = frameWidth;
        GUI.DrawTexture(lineArea, boxOutlineTexture); // Top line

        lineArea.y = area.yMax - frameWidth;
        GUI.DrawTexture(lineArea, boxOutlineTexture); // Bottom line

        lineArea = area;
        lineArea.width = frameWidth;
        GUI.DrawTexture(lineArea, boxOutlineTexture); // Left line

        lineArea.x = area.xMax - frameWidth;
        GUI.DrawTexture(lineArea, boxOutlineTexture); // Right line
    }


    private static void DrawLabel(Rect position, string text)
    {
        //ScreenLog.Log("I AM DRAWING A LABEL!!");
        //ScreenLog.Log(position.ToString());
        GUI.Label(position, text, labelStyle);
    }

    private Texture2D Scale(Texture2D texture, int imageSize)
    {
        var scaled = TextureTools.scaled(texture, imageSize, imageSize, FilterMode.Bilinear);
        return scaled;
    }


    private Color32[] Rotate(Color32[] pixels, int width, int height)
    {
        var rotate = TextureTools.RotateImageMatrix(
                pixels, width, height, 90);
        // var flipped = TextureTools.FlipYImageMatrix(rotate, width, height);
        //flipped =  TextureTools.FlipXImageMatrix(flipped, width, height);
        // return flipped;
        return rotate;
    }



}
