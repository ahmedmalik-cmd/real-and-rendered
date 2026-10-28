using UnityEngine;

/// <summary>
/// Mouse-controlled orbit camera for the Game view.
///   Right-mouse drag  — orbit (yaw + pitch)
///   Scroll wheel      — zoom (dolly)
///   Middle-mouse drag — pan (truck + pedestal)
/// </summary>
public class OrbitCamera : MonoBehaviour
{
    [Header("Target")]
    public Vector3 target = new Vector3(0f, 1f, 0f);

    [Header("Orbit")]
    public float yaw         = 0f;
    public float pitch       = 25f;
    public float distance    = 5f;

    [Header("Sensitivity")]
    public float orbitSpeed  = 180f;   // degrees per screen-width drag
    public float panSpeed    = 3f;     // world-units per screen-width drag
    public float zoomSpeed   = 4f;     // world-units per scroll click

    [Header("Limits")]
    public float minPitch    = -80f;
    public float maxPitch    =  80f;
    public float minDistance =  0.5f;
    public float maxDistance = 30f;

    void LateUpdate()
    {
        float dx = Input.GetAxis("Mouse X");
        float dy = Input.GetAxis("Mouse Y");

        // Right-mouse: orbit
        if (Input.GetMouseButton(1))
        {
            yaw   += dx * orbitSpeed * Time.deltaTime;
            pitch -= dy * orbitSpeed * Time.deltaTime;
            pitch  = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        // Middle-mouse: pan
        if (Input.GetMouseButton(2))
        {
            var rot   = Quaternion.Euler(pitch, yaw, 0f);
            var right = rot * Vector3.right;
            var up    = rot * Vector3.up;
            target   -= right * dx * panSpeed * Time.deltaTime;
            target   -= up    * dy * panSpeed * Time.deltaTime;
        }

        // Scroll: zoom
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0f)
            distance = Mathf.Clamp(distance - scroll * zoomSpeed, minDistance, maxDistance);

        // Apply
        var q         = Quaternion.Euler(pitch, yaw, 0f);
        transform.position = target + q * new Vector3(0f, 0f, -distance);
        transform.rotation = q;
    }
}
