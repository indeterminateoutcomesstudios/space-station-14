﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BKSystem.IO;
using SS13_Shared;
using SS13.IoC;
using ServerInterfaces.Atmos;
using ServerServices.Log;

namespace ServerServices.Atmos
{
    public class GasMixture
    {
        private float temperature = 293.15f; // Normal room temp in K
        private float nextTemperature = 0;
        private float volume = 2.0f; // in m^3
        private bool burning = false;
        private bool exposed = false; // Have we been exposed to a source of ignition?

        public Dictionary<GasType, float> gasses; // Type, moles
        public Dictionary<GasType, float> nextGasses;
        public Dictionary<GasType, float> lastSentGasses;

        public GasMixture()
        {
            InitGasses();
        }

        private void InitGasses()
        {
            gasses = new Dictionary<GasType, float>();
            nextGasses = new Dictionary<GasType, float>();
            lastSentGasses = new Dictionary<GasType, float>();

            var gasTypes = Enum.GetValues(typeof(GasType));
            foreach (GasType g in gasTypes)
            {
                gasses.Add(g, 0);
                nextGasses.Add(g, 0);
                lastSentGasses.Add(g, 0);
            }
        }

        public void Update()
        {
            foreach (var ng in nextGasses)
            {
                gasses[ng.Key] = ng.Value;
            }

            temperature += nextTemperature;
            nextTemperature = 0;
            if (temperature < 0)    // This shouldn't happen unless someone fucks with the temperature directly
                temperature = 0;
            exposed = false;

        }

        public void SetNextTemperature(float temp)
        {
            nextTemperature += temp;
        }

        public void AddNextGas(float amount, GasType gas)
        {
            nextGasses[gas] += amount;

            if (nextGasses[gas] < 0)    // This shouldn't happen unless someone calls this directly but lets just make sure
                nextGasses[gas] = 0;
        }

        public void Diffuse(GasMixture a, float factor = 8)
        {
            foreach (var gas in a.gasses)
            {
                var amount = (gas.Value - gasses[gas.Key]) / factor;
                AddNextGas(amount, gas.Key);
                a.AddNextGas(-amount, gas.Key);
            }

            ShareTemp(a, factor);
        }

        public void ShareTemp(GasMixture a, float factor = 8)
        {
            float HCCell = HeatCapacity * TotalMass;
            float HCa = a.HeatCapacity * a.TotalMass;
            float energyFlow = a.Temperature - Temperature;

            if (energyFlow > 0.0f)
            {
                energyFlow *= HCa;
            }
            else
            {
                energyFlow *= HCCell;
            }

            energyFlow *= (1 / factor);
            SetNextTemperature((energyFlow / HCCell));
            a.SetNextTemperature(-(energyFlow / HCa));
        }

        public void Expose()
        {
            exposed = true;
        }

        public void Burn()
        {
            var am = IoCManager.Resolve<IAtmosManager>();
            if (!Burning) // If we're not burning lets see if we can start due to autoignition
            {
                foreach (GasType g in gasses.Keys)
                {
                    float ait = am.GetGasProperties(g).AutoignitionTemperature;
                    if (ait > 0.0f && temperature > ait) // If our temperature is high enough to autoignite then we're burning now
                    {
                        burning = true;
                        continue;
                    }
                }
            }

            float energy_released = 0.0f;

            if (Burning || exposed) // We're going to try burning some of our gasses
            {
                float cAmount = 0.0f;
                float oAmount = 0.0f;
                foreach (GasType g in nextGasses.Keys)
                {
                    if (am.GetGasProperties(g).Combustable)
                    {
                        cAmount += gasses[g];
                    }
                    if (am.GetGasProperties(g).Oxidant)
                    {
                        oAmount += gasses[g];
                    }
                }

                if (oAmount > 0.0001f && cAmount > 0.0001f && Pressure > 10)
                {
                    float ratio = Math.Min(1f, oAmount / cAmount); // This is how much of each gas we can burn as that's how much oxidant we have free
                    ratio /= 3; // Lets not just go mental and burn everything in one go because that's dumb
                    float amount = 0.0f;

                    foreach (GasType g in gasses.Keys)
                    {
                        amount = gasses[g] * ratio;
                        if (am.GetGasProperties(g).Combustable)
                        {
                            AddNextGas(-amount, g);
                            AddNextGas(amount, GasType.CO2);
                            energy_released += (am.GetGasProperties(g).SpecificHeatCapacity * 2000 * amount); // This is COMPLETE bullshit non science but whatever
                        
                        }
                        if (am.GetGasProperties(g).Oxidant)
                        {
                            AddNextGas(-amount, g);
                            AddNextGas(amount, GasType.CO2);
                            energy_released += (am.GetGasProperties(g).SpecificHeatCapacity * 2000 * amount); // This is COMPLETE bullshit non science but whatever
                        }
                    }

                }
            }

            if (energy_released > 0)
            {
                SetNextTemperature(energy_released *= HeatCapacity); // This is COMPLETE bullshit non science but whatever
                burning = true;
            }
            else // Nothing burnt here so we're not on fire
            {
                burning = false;
            }
                
        }

        public float TotalGas
        {
            get
            {
                float total = 0;
                foreach (var gas in gasses)
                {
                    total += gas.Value;
                }

                return total;
            }
        }

        public float Temperature
        {
            get
            {
                return temperature;
            }
        }

        // P = nRT / V
        public float Pressure
        {
            get
            {
                return ((TotalGas * 8.314f * Temperature) / Volume);
            }
        }

        public float Volume
        {
            get
            {
                return volume;
            }
            set
            {
                volume = value;
            }
        }

        public float MassOf(GasType gas)
        {
            return (IoCManager.Resolve<IAtmosManager>().GetGasProperties(gas).MolecularMass * gasses[gas]);
        }

        public float HeatCapacity
        {
            get
            {
                float SHC = 0.0f;
                foreach (GasType g in gasses.Keys)
                {
                    SHC += (gasses[g] * IoCManager.Resolve<IAtmosManager>().GetGasProperties(g).SpecificHeatCapacity);
                }
                
                return SHC;
            }
        }

        public float TotalMass
        {
            get
            {
                float mass = 0.0f;

                foreach (GasType g in gasses.Keys)
                {
                    mass += (gasses[g] * IoCManager.Resolve<IAtmosManager>().GetGasProperties(g).MolecularMass);
                }

                return mass;
            }
        }

        public bool Burning
        {
            get
            {
                return burning;
            }
        }

        
    }
}