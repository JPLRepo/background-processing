using UnityEngine;
using System.Collections.Generic;
using System;

namespace BackgroundProcessing.ResourceHandlers {
	using DebugLevel = AddonConfig.DebugLevel;

	class SolarPanel : ResourceModuleHandler {
		public String resourceName { get; private set; }
		public float chargeRate { get; private set; }
		public FloatCurve powerCurve { get; private set; }
		public Vector3d position { get; private set; }
		public Quaternion orientation { get; private set; }
		public Vector3d solarNormal { get; private set; }
		public Vector3d pivotAxis { get; private set; }
		public bool tracks { get; private set; }
		public bool usesCurve { get; private set; }
		public FloatCurve tempCurve { get; private set; }
		public float temperature { get; private set; }

		public SolarPanel(
			String rN,
			float cR,
			FloatCurve pc,
			Vector3d p,
			Quaternion o,
			Vector3d sn,
			Vector3d pa,
			bool t,
			bool uC,
			FloatCurve tE,
			float tmp
		) {
			resourceName = rN;
			chargeRate = cR;
			powerCurve = pc;
			position = p;
			orientation = o;
			solarNormal = sn;
			pivotAxis = pa;
			tracks = t;
			usesCurve = uC;
			tempCurve = tE;
			temperature = tmp;
		}

		// return true if the ray 'dir' starting at 'p' doesn't hit 'body'
		// - p: ray origin
		// - dir: ray direction
		// - body: obstacle
		public static bool Raytrace(Vector3d p, Vector3d dir, CelestialBody body) {
			// ray from origin to body center
			Vector3d diff = body.position - p;

			// projection of origin->body center ray over the raytracing direction
			double k = Vector3d.Dot(diff, dir);

			// the ray doesn't hit body if its minimal analytical distance along the ray is less than its radius
			return k < 0.0 || (dir * k - diff).magnitude > body.Radius;
		}

		// return true if the body is visible from the vessel
		// - vessel: origin
		// - body: target
		// - dir: will contain normalized vector from vessel to body
		// - dist: will contain distance from vessel to body surface
		// - return: true if visible, false otherwise
		public static bool RaytraceBody(Vessel vessel, CelestialBody body, out Vector3d dir, out double dist) {
			// shortcuts
			CelestialBody mainbody = vessel.mainBody;
			CelestialBody refbody = mainbody.referenceBody;

			// generate ray parameters
			Vector3d vessel_pos = VesselPosition(vessel);
			dir = body.position - vessel_pos;
			dist = dir.magnitude;
			dir /= dist;
			dist -= body.Radius;

			// raytrace
			return (body == mainbody || Raytrace(vessel_pos, dir, mainbody))
				&& (body == refbody || refbody == null || Raytrace(vessel_pos, dir, refbody));
		}

		public static bool Landed(Vessel v) {
			return v.loaded ? (v.Landed || v.Splashed) : (v.protoVessel.landed || v.protoVessel.splashed);
		}

		public static Vector3d VesselPosition(Vessel v) {
			// the issue
			//   - GetWorldPos3D() return mainBody position for a few ticks after scene changes
			//   - we can detect that, and fall back to evaluating position from the orbit
			//   - orbit is not valid if the vessel is landed, and for a tick on prelauch/staging/decoupling
			//   - evaluating position from latitude/longitude work in all cases, but is probably the slowest method

			// get vessel position
			Vector3d pos = v.GetWorldPos3D();

			// during scene changes, it will return mainBody position
			if (Vector3d.SqrMagnitude(pos - v.mainBody.position) < 1.0) {
				// try to get it from orbit
				pos = v.orbit.getPositionAtUT(Planetarium.GetUniversalTime());

				// if the orbit is invalid (landed, or 1 tick after prelauch/staging/decoupling)
				if (double.IsNaN(pos.x)) {
					// get it from lat/long (work even if it isn't landed)
					pos = v.mainBody.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
				}
			}

			// victory
			return pos;
		}

		// return sun luminosity
		private static double semiMajorAxis;

		public static double SolarLuminosity {
			get {
				// note: it is 0 before loading first vessel in a game session, we compute it in that case
				if (PhysicsGlobals.SolarLuminosity <= Double.Epsilon) {
					semiMajorAxis = FlightGlobals.GetHomeBody().orbit.semiMajorAxis;
					return semiMajorAxis * semiMajorAxis * 12.566370614359172 * PhysicsGlobals.SolarLuminosityAtHome;
				}
				return PhysicsGlobals.SolarLuminosity;
			}
		}


		public static bool HasResourceGenerationData(
			PartModule m,
			ProtoPartModuleSnapshot s,
			Dictionary<string, List<ResourceModuleHandler>> resourceData,
			HashSet<string> interestingResources
		) {
			return s.moduleValues.GetValue("deployState") == ModuleDeployableSolarPanel.DeployState.EXTENDED.ToString();
		}

		public static List<ResourceModuleHandler> GetResourceGenerationData(
			PartModule m,
			ProtoPartSnapshot part,
			Dictionary<string, List<ResourceModuleHandler>> resourceData,
			HashSet<string> interestingResources
		) {
			List<ResourceModuleHandler> ret = new List<ResourceModuleHandler>();

			ModuleDeployableSolarPanel p = (ModuleDeployableSolarPanel)m;

			if (interestingResources.Contains(p.resourceName)) {
				Transform panel = p.part.FindModelTransform(p.secondaryTransformName);
				Transform pivot = p.part.FindModelTransform(p.pivotName);
				bool sunTracking = p.isTracking && p.trackingMode == ModuleDeployablePart.TrackingMode.SUN;

				ret.Add(new SolarPanel(p.resourceName, p.chargeRate, p.powerCurve, part.position, part.rotation, panel.forward, pivot.up, sunTracking, p.useCurve, p.temperatureEfficCurve, (float)part.temperature));
			}

			return ret;
		}

		public override HashSet<ProtoPartResourceSnapshot> HandleResource(Vessel v, VesselData data, HashSet<ProtoPartResourceSnapshot> modified) {
			BackgroundProcessing.Debug("Panel data, doing solar panel calcs", DebugLevel.ALL);

			Vector3d sun_dir;
			double sun_dist;
			bool in_sunlight = SolarPanel.RaytraceBody(v, FlightGlobals.Bodies[0], out sun_dir, out sun_dist);
			Vector3d partPos = VesselPosition(v) + position;

			double orientationFactor = 1;

			if (tracks) {
				Vector3d localPivot = (v.transform.rotation * orientation * pivotAxis).normalized;
				orientationFactor = Math.Cos(Math.PI / 2.0 - Math.Acos(Vector3d.Dot(localPivot, sun_dir)));
			}
			else {
				Vector3d localSolarNormal = (v.transform.rotation * orientation * solarNormal).normalized;
				orientationFactor = Vector3d.Dot(localSolarNormal, sun_dir);
			}

			orientationFactor = Math.Max(orientationFactor, 0);

			if (in_sunlight) {
				double solarFlux = SolarPanel.SolarLuminosity / (12.566370614359172 * sun_dist * sun_dist);
				BackgroundProcessing.Debug("Pre-atmosphere flux: " + solarFlux + ", pre-atmosphere distance: " + sun_dist + ", solar luminosity: " + PhysicsGlobals.SolarLuminosity, DebugLevel.ALL);

				double staticPressure = v.mainBody.GetPressure(v.altitude);
				BackgroundProcessing.Debug("Static pressure: " + staticPressure, DebugLevel.ALL);

				if (staticPressure > 0.0) {
					double density = v.mainBody.GetDensity(staticPressure, temperature);
					BackgroundProcessing.Debug("density: " + density, DebugLevel.ALL);
					Vector3 up = FlightGlobals.getUpAxis(v.mainBody, v.vesselTransform.position).normalized;
					double sunPower = v.mainBody.radiusAtmoFactor * Vector3d.Dot(up, sun_dir);
					double sMult = v.mainBody.GetSolarPowerFactor(density);
					if (sunPower < 0) {
						sMult /= Math.Sqrt(2.0 * v.mainBody.radiusAtmoFactor + 1.0);
					}
					else {
						sMult /= Math.Sqrt(sunPower * sunPower + 2.0 * v.mainBody.radiusAtmoFactor + 1.0) - sunPower;
					}

					BackgroundProcessing.Debug("Atmospheric flux adjustment: " + sMult, DebugLevel.ALL);
					solarFlux *= sMult;

					BackgroundProcessing.Debug("Vessel solar flux: " + v.solarFlux, DebugLevel.ALL);
				}
				else { BackgroundProcessing.Debug("No need for atmospheric adjustment", DebugLevel.ALL); }

				float multiplier = 1;
				if (usesCurve) { multiplier = powerCurve.Evaluate((float)FlightGlobals.Bodies[0].GetAltitude(partPos)); }
				else { multiplier = (float)(solarFlux / PhysicsGlobals.SolarLuminosityAtHome); }

				BackgroundProcessing.Debug("Resource rate: " + chargeRate, DebugLevel.ALL);
				BackgroundProcessing.Debug("Vessel " + v.vesselName + " solar panel, orientation factor: " + orientationFactor + ", temperature: " + temperature + " solar flux: " + solarFlux, DebugLevel.ALL);
				float tempFactor = tempCurve.Evaluate(temperature);

				if (!BackgroundProcessing.config.solarOrientationMatters) { orientationFactor = 1; BackgroundProcessing.Debug("Orientation disabled in config file", DebugLevel.ALL); }
				if (!BackgroundProcessing.config.solarTemperatureMatters) { tempFactor = 1; BackgroundProcessing.Debug("Temperature disabled in config file", DebugLevel.ALL); }

				float resourceAmount = chargeRate * (float)orientationFactor * tempFactor * multiplier;
				BackgroundProcessing.Debug("Vessel " + v.vesselName + ", adding " + resourceAmount + " " + resourceName + " over time " + TimeWarp.fixedDeltaTime, DebugLevel.ALL);
				modified = AddResource(data, resourceAmount * TimeWarp.fixedDeltaTime, resourceName, modified);
			}
			else {
				BackgroundProcessing.Debug("Can't see Kerbol", DebugLevel.ALL);
			}

			return modified;
		}
	}
}