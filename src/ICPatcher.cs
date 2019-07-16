using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Kopernicus;
using Kopernicus.Components;
using Kopernicus.ConfigParser;
using Kopernicus.Configuration;

namespace InterstellarConsortium
{
    public class ICPatcher
    {
        /// <summary>
        /// All bodies that were loaded by Kopernicus
        /// </summary>
        private static readonly List<Body> Bodies = new List<Body>();
        
        /// <summary>
        /// Gets invoked after Kopernicus loaded nothing more than the name of the body. If the
        /// body is named Kerbin or Sun, we change it to a random value internally, to prevent
        /// collisions when loading multiple otherwise conflicting mods.
        /// </summary>
        public static void OnBodyApply(Body body, ConfigNode node)
        {
            if (body.Name == "Kerbin")
            {
                // We found a Kerbin. Since the homeworld is dynamically managed by us, rename them to something safe
                RenameBody(body, "Kerbin::" + Guid.NewGuid());
            }

            if (body.Name == "Sun")
            {
                // We found a Sun. Same reason as for Kerbin, we want to assign those names
                RenameBody(body, "Sun::" + Guid.NewGuid());
            }
            
            // Check if the name of the body already exists
            if (Bodies.Any(b => b.Name == body.Name))
            {
                RenameBody(body, body.Name + "::" + Guid.NewGuid());
            }
        }

        /// <summary>
        /// Changes the name of a body as if it would be created with the new name
        /// </summary>
        public static void RenameBody(Body body, String name)
        {
            RenameBody(body.GeneratedBody, body.Name = name);
        }

        /// <summary>
        /// Changes the name of a body as if it would be created with the new name
        /// </summary>
        public static void RenameBody(PSystemBody generatedBody, String name)
        {
            // Patch the game object names in the template
            String oldName = generatedBody.name;
            generatedBody.name = name;
            generatedBody.celestialBody.bodyName = name;
            generatedBody.celestialBody.transform.name = name;
            generatedBody.celestialBody.bodyTransform.name = name;
            generatedBody.scaledVersion.name = name;
            if (generatedBody.pqsVersion != null)
            {
                generatedBody.pqsVersion.name = name;
                generatedBody.pqsVersion.gameObject.name = name;
                generatedBody.pqsVersion.transform.name = name;
                foreach (PQS p in generatedBody.pqsVersion.GetComponentsInChildren<PQS>(true))
                    p.name = p.name.Replace(oldName, name);
                generatedBody.celestialBody.pqsController = generatedBody.pqsVersion;
            }
        }
        
        /// <summary>
        /// Gets invoked after a body has been loaded successfully. We use it to collect references to all bodies
        /// because Kopernicus doesn't have an API that allows us to access all bodies between loading and finalizing
        /// </summary>
        public static void OnLoaderLoadBody(Body body, ConfigNode node)
        {
            Bodies.Add(body);
        }

        /// <summary>
        /// Gets invoked when all bodies were successfully loaded
        /// </summary>
        public static void OnLoaderLoadedAllBodies(Loader loader, ConfigNode node)
        {
            // Grab home star and home planet
            CelestialBody[] cbs = Bodies.Select(b => b.CelestialBody).ToArray();
            CelestialBody star = UBI.GetBody(InterstellarSettings.Instance.HomeStar, cbs);
            if (!star.Has("IC:HomePlanet"))
            {
                throw new Exception("The home star must have a home planet defined.");
            }

            CelestialBody planet = UBI.GetBody(InterstellarSettings.Instance.HomePlanet ?? star.Get("IC:HomePlanet", ""), cbs);

            // Grab the position of the home star
            Vector3d position = star.Get("IC:Position", Vector3d.zero);

            // Fix the naming of home star and home planet
            Body starBody = Bodies.Find(b => b.CelestialBody == star);
            Body planetBody = Bodies.Find(b => b.CelestialBody == planet);
            RenameBody(starBody, "Sun");
            RenameBody(planetBody, "Kerbin");
            planet.isHomeWorld = true;

            // Since this body is the center of the universe, remove its orbit
            UnityEngine.Object.DestroyImmediate(starBody.GeneratedBody.celestialBody.GetComponent<OrbitRendererUpdater>());
            UnityEngine.Object.DestroyImmediate(starBody.GeneratedBody.orbitDriver);
            UnityEngine.Object.DestroyImmediate(starBody.GeneratedBody.orbitRenderer);
            starBody.Orbit = null;
            
            // Trigger the KSC mover
            WithBody(planetBody, () => { planetBody.Pqs = new PQSLoader(); });

            // Go through each body, and calculate the orbits of the stars
            foreach (Body body in Bodies)
            {
                // Don't work with non-IC bodies
                if (!body.CelestialBody.Has("IC:Position"))
                {
                    continue;
                }
                
                // We already edited the home star
                if (body.CelestialBody == star)
                {
                    continue;
                }

                // Apply the SOI
                if (body.CelestialBody.Has("IC:SOI"))
                {
                    body.Properties.SphereOfInfluence = body.CelestialBody.Get("IC:SOI", 0d) * InterstellarSettings.Instance.KI;
                }

                // Calculate the real position of the star
                Vector3d realPosition = body.CelestialBody.Get("IC:Position", Vector3d.zero) - position;

                // Apply the orbital parameters
                WithBody(body, () => 
                {
                    body.Orbit = new OrbitLoader();
                    body.Orbit.SemiMajorAxis =
                        Math.Pow(
                            Math.Pow(realPosition.x, 2) + Math.Pow(realPosition.y, 2) + Math.Pow(realPosition.z, 2),
                            0.5) * InterstellarSettings.Instance.KI;
                    body.Orbit.Eccentricity = 0;
                    body.Orbit.ArgumentOfPeriapsis = 90;
                    body.Orbit.MeanAnomalyAtEpoch = 0;
                    body.Orbit.Inclination =
                        Math.Atan(realPosition.z /
                                  Math.Pow(Math.Pow(realPosition.x, 2) + Math.Pow(realPosition.y, 2), 0.5)) / Math.PI *
                        180;
                    body.Orbit.LongitudeOfAscendingNode = CalculateLAN(realPosition.x, realPosition.y);
                    body.Orbit.Period = Double.MaxValue;
                    body.Orbit.ReferenceBody = UBI.GetUBI(star);

                    // This is a bit of a hack, because 1.3.1 and 1.4.5+ use different enums for
                    // that value, and i want IC to be compatible with all versions
                    try
                    {
                        PropertyInfo orbitMode = typeof(OrbitLoader).GetProperty("mode");
                        Type parserType = orbitMode.PropertyType;
                        MethodInfo setFromString = parserType.GetMethod("SetFromString");
                        Object parser = orbitMode.GetValue(body.Orbit, null);
                        setFromString.Invoke(parser, new[] {"OFF"});
                        orbitMode.SetValue(body.Orbit, parser, null);
                    }
                    catch
                    {
                        // ignored
                    }

                    // Load additional patches
                    if (body.CelestialBody.Has("IC:OrbitPatches"))
                    {
                        Parser.LoadObjectFromConfigurationNode(body.Orbit,
                            body.CelestialBody.Get<ConfigNode>("IC:OrbitPatches"));
                    }
                });
            }

        }

        public static Double CalculateLAN(Double x, Double y)
        {
            Double output = Math.Atan(y / x);
            if(x < 0)
            {
                output += Math.PI;
            }
            else if (y < 0)
            {
                output += 2 * Math.PI;
            }
            return output / Math.PI * 180;
        }

        public static void WithBody(Body body, Action task)
        {
            Parser.SetState("Kopernicus:currentBody", () => body);
            task();
            Parser.ClearState("Kopernicus:currentBody");
        }
    }
}