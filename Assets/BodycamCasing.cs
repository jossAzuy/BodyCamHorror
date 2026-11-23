using UnityEngine;

namespace Bodycam
{
    /// <summary>
    /// Added automatically to every ejected casing by BodycamShootFeedback.
    /// Reports impacts so the owner can play the "tink" sound.
    /// </summary>
    public class BodycamCasing : MonoBehaviour
    {
        public Rigidbody Body;
        public BodycamShootFeedback Owner;
        public float SpawnTime;

        private int _bounces;
        private float _lastImpact = -1f;

#if UNITY_6000_0_OR_NEWER
        private static PhysicsMaterial _bouncy;
#else
        private static PhysicMaterial _bouncy;
#endif

        public void Launch(Vector3 velocity, Vector3 angularVelocity)
        {
            _bounces = 0;
            _lastImpact = -1f;
            SpawnTime = Time.time;

#if UNITY_6000_0_OR_NEWER
            Body.linearVelocity = velocity;
#else
            Body.velocity = velocity;
#endif
            Body.angularVelocity = angularVelocity;
        }

        /// <summary>Gives the collider a little bounce so casings can skip on the floor.</summary>
        public static void ApplyBouncyMaterial(Collider col)
        {
            if (_bouncy == null)
            {
#if UNITY_6000_0_OR_NEWER
                _bouncy = new PhysicsMaterial("BodycamCasing")
                {
                    bounciness = 0.45f,
                    dynamicFriction = 0.4f,
                    staticFriction = 0.5f,
                    bounceCombine = PhysicsMaterialCombine.Maximum
                };
#else
                _bouncy = new PhysicMaterial("BodycamCasing")
                {
                    bounciness = 0.45f,
                    dynamicFriction = 0.4f,
                    staticFriction = 0.5f,
                    bounceCombine = PhysicMaterialCombine.Maximum
                };
#endif
            }
            col.sharedMaterial = _bouncy;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (Owner == null) return;

            float speed = collision.relativeVelocity.magnitude;
            if (speed < Owner.MinImpactSpeed) return;
            if (Time.time - _lastImpact < 0.05f) return;   // avoid double triggers on a single bounce
            _lastImpact = Time.time;

            Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
            Owner.PlayCasingImpact(point, speed, _bounces++);
        }
    }
}
