//The MIT License (MIT)

//Those portions created by va3c authors are provided with the following copyright:

//Copyright (c) 2014 va3c

//Those portions created by Thornton Tomasetti employees are provided with the following copyright:

//Copyright (c) 2015 Thornton Tomasetti

//Those portions created by CodeCave employees are provided with the following copyright:

//Copyright (c) 2017 CodeCave

//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.

#region Namespaces

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using System.Reflection;

#endregion // Namespaces

namespace RvtVa3c
{
    // Done:
    // Check instance transformation
    // Support transparency
    // Add scaling for Theo [(0,0),(20000,20000)]
    // Implement the external application button
    // Implement element properties
    // Eliminate multiple materials 
    // Prompt user for output file name and location
    // Eliminate null element properties, i.e. useless 
    //     JSON userData entries
    // TODO:
    // Check for file size
    // Instance/Type reuse

    // ReSharper disable once InconsistentNaming
    public class Va3cExportContext : IExportContext
    {
        /// <summary>
        /// Scale entire top level BIM object node in JSON
        /// output. A scale of 1.0 will output the model in 
        /// millimeters. Currently we scale it to decimeters
        /// so that a typical model has a chance of fitting 
        /// into a cube with side length 100, i.e. 10 meters.
        /// </summary>
        readonly double _scale_bim = 1.0;

        /// <summary>
        /// Scale applied to each vertex in each individual 
        /// BIM element. This can be used to scale the model 
        /// down from millimeters to meters, e.g.
        /// Currently we stick with millimeters after all
        /// at this level.
        /// </summary>
        readonly double _scale_vertex = 1.0;

        /// <summary>
        /// If true, switch Y and Z coordinate 
        /// and flip X to negative to convert from
        /// Revit coordinate system to standard 3d
        /// computer graphics coordinate system with
        /// Z pointing out of screen, X towards right,
        /// Y up.
        /// </summary>
        readonly bool _switchCoordinates = true;

        #region VertexLookupXyz

        #endregion // VertexLookupXyz

        #region VertexLookupInt

        /// <inheritdoc />
        /// <summary>
        /// An integer-based 3D point class.
        /// </summary>
        private class PointInt : IComparable<PointInt>
        {
            public long X { get; }
            public long Y { get; }
            public long Z { get; }

            //public PointInt( int x, int y, int z )
            //{
            //  X = x;
            //  Y = y;
            //  Z = z;
            //}

            /// <summary>
            /// Consider a Revit length zero 
            /// if is smaller than this.
            /// </summary>
            const double EPS = 1.0e-9;

            /// <summary>
            /// Conversion factor from feet to millimeters.
            /// </summary>
            const double FEET_TO_MM = 25.4 * 12;

            /// <summary>
            /// Conversion a given length value 
            /// from feet to millimeter.
            /// </summary>
            private static long ConvertFeetToMillimetres(double d)
            {
                if (0 < d)
                {
                    return EPS > d
                        ? 0
                        : (long) (FEET_TO_MM * d + 0.5);

                }
                return EPS > -d
                    ? 0
                    : (long) (FEET_TO_MM * d - 0.5);
            }

            public PointInt(XYZ p, bool switchCoordinates)
            {
                X = ConvertFeetToMillimetres(p.X);
                Y = ConvertFeetToMillimetres(p.Y);
                Z = ConvertFeetToMillimetres(p.Z);

                if (!switchCoordinates) return;
                X = -X;
                var tmp = Y;
                Y = Z;
                Z = tmp;
            }

            public int CompareTo(PointInt a)
            {
                var d = X - a.X;

                if (0 != d)
                    return (0 == d) ? 0 : ((0 < d) ? 1 : -1);

                d = Y - a.Y;

                if (0 == d)
                {
                    d = Z - a.Z;
                }
                return (0 == d) ? 0 : ((0 < d) ? 1 : -1);
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// A vertex lookup class to eliminate 
        /// duplicate vertex definitions.
        /// </summary>
        private class VertexLookupInt : Dictionary<PointInt, int>
        {
            #region PointIntEqualityComparer

            /// <inheritdoc />
            /// <summary>
            /// Define equality for integer-based PointInt.
            /// </summary>
            private class PointIntEqualityComparer : IEqualityComparer<PointInt>
            {
                public bool Equals(PointInt p, PointInt q)
                {
                    return p != null && 0 == p.CompareTo(q);
                }

                public int GetHashCode(PointInt p)
                {
                    return (p.X + "," + p.Y + "," + p.Z).GetHashCode();
                }
            }

            #endregion // PointIntEqualityComparer

            public VertexLookupInt()
                : base(new PointIntEqualityComparer())
            {
            }

            /// <summary>
            /// Return the index of the given vertex,
            /// adding a new entry if required.
            /// </summary>
            public int AddVertex(PointInt p)
            {
                return ContainsKey(p)
                    ? this[p]
                    : this[p] = Count;
            }
        }

        #endregion // VertexLookupInt

        private Document _currentDoc;
        private readonly Document _doc;
        private readonly string _filename;
        private Va3cContainer _container;

        private Dictionary<string, Va3cContainer.Va3cMaterial> _materials;
        private Dictionary<string, Va3cContainer.Va3cObject> _objects;
        private Dictionary<string, Va3cContainer.Va3cGeometry> _geometries;
        private Dictionary<string, string> _viewsAndLayersDict;
        private List<string> _layerList;

        private Va3cContainer.Va3cObject _currentElement;

        // Keyed on material uid to handle several materials per element:

        private Dictionary<string, Va3cContainer.Va3cObject> _currentObject;
        private Dictionary<string, Va3cContainer.Va3cGeometry> _currentGeometry;
        private Dictionary<string, VertexLookupInt> _vertices;

        private readonly Stack<ElementId> _elementStack = new Stack<ElementId>();
        private readonly Stack<Transform> _transformationStack = new Stack<Transform>();

        private string _currentMaterialUid;

        public string myjs;

        private Va3cContainer.Va3cObject CurrentObjectPerMaterial => _currentObject[_currentMaterialUid];

        private Va3cContainer.Va3cGeometry CurrentGeometryPerMaterial => _currentGeometry[_currentMaterialUid];

        private VertexLookupInt CurrentVerticesPerMaterial => _vertices[_currentMaterialUid];

        private Transform CurrentTransform => _transformationStack.Peek();

        public override string ToString()
        {
            return myjs;
        }

        /// <summary>
        /// Set the current material
        /// </summary>
        private void SetCurrentMaterial(string uidMaterial)
        {
            if (!_materials.ContainsKey(uidMaterial))
            {
                if (_currentDoc?.GetElement(uidMaterial) is Material material)
                {
                    var m = new Va3cContainer.Va3cMaterial
                    {
                        UUID = uidMaterial,
                        Name = material.Name,
                        Type = "MeshLambertMaterial",
                        Color = Util.ColorToInt(material.Color)
                    };

                    m.Ambient = m.Color;
                    m.Emissive = 0;
                    m.Opacity = 0.01 * (100 - material
                                            .Transparency
                                ); // Revit has material.Transparency in [0,100], three.js expects Opacity in [0.0,1.0]
                    m.Transparent = 0 < material.Transparency;
                    m.Shading = 1;
                    m.Wireframe = false;

                    _materials.Add(uidMaterial, m);
                }
            }
            _currentMaterialUid = uidMaterial;

            var uidPerMaterial = _currentElement.UUID + "-" + uidMaterial;

            if (!_currentObject.ContainsKey(uidMaterial))
            {
                Debug.Assert(!_currentGeometry.ContainsKey(uidMaterial), "expected same keys in both");

                _currentObject.Add(uidMaterial, new Va3cContainer.Va3cObject());
                CurrentObjectPerMaterial.Name = _currentElement.Name;
                CurrentObjectPerMaterial.Geometry = uidPerMaterial;
                CurrentObjectPerMaterial.Material = _currentMaterialUid;
                CurrentObjectPerMaterial.Matrix = new double[] {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
                CurrentObjectPerMaterial.Type = "Mesh";
                CurrentObjectPerMaterial.UUID = uidPerMaterial;
            }

            if (!_currentGeometry.ContainsKey(uidMaterial))
            {
                _currentGeometry.Add(uidMaterial, new Va3cContainer.Va3cGeometry());
                CurrentGeometryPerMaterial.UUID = uidPerMaterial;
                CurrentGeometryPerMaterial.Type = "Geometry";
                CurrentGeometryPerMaterial.Data = new Va3cContainer.Va3cGeometryData
                {
                    Faces = new List<int>(),
                    Vertices = new List<double>(),
                    Normals = new List<double>(),
                    UVs = new List<double>(),
                    Visible = true,
                    CastShadow = true,
                    ReceiveShadow = false,
                    DoubleSided = true,
                    Scale = 1.0
                };
            }

            if (!_vertices.ContainsKey(uidMaterial))
            {
                _vertices.Add(uidMaterial, new VertexLookupInt());
            }
        }

        public Va3cExportContext(Document document, string filename)
        {
            _doc = document;
            _currentDoc = document;
            _filename = filename;
        }

        public bool Start()
        {
            _materials = new Dictionary<string, Va3cContainer.Va3cMaterial>();
            _geometries = new Dictionary<string, Va3cContainer.Va3cGeometry>();
            _objects = new Dictionary<string, Va3cContainer.Va3cObject>();

            _viewsAndLayersDict = new Dictionary<string, string>();
            _layerList = new List<string>();

            _transformationStack.Push(Transform.Identity);

            var assembly = Assembly.GetExecutingAssembly();
            var assemblyVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion ?? "0.0";
            _container = new Va3cContainer
            {
                Metadata = new Va3cContainer.Va3cMetadata
                {
                    Type = "Object",
                    Version = assemblyVersion,
                    Generator = "Revit Va3c exporter"
                },
                Geometries = new List<Va3cContainer.Va3cGeometry>(),
                Object = new Va3cContainer.Va3cObject
                {
                    UUID = _currentDoc.ActiveView.UniqueId,
                    Name = "BIM " + _currentDoc.Title,
                    Type = "Scene",
                    Matrix = new[]
                    {
                        _scale_bim, 0, 0, 0,
                        0, _scale_bim, 0, 0,
                        0, 0, _scale_bim, 0,
                        0, 0, 0, _scale_bim
                    }
                }
            };

            return true;
        }

        public void Finish()
        {
            // Finish populating scene
            _container.Materials = _materials.Values.ToList();
            _container.Geometries = _geometries.Values.ToList();
            _container.Object.Children = _objects.Values.ToList();

            if (Command.cameraNames.Count > 0)
            {
                //create an empty string to append the list of views
                var viewList = Command.cameraNames[0] + "," + Command.cameraPositions[0] + "," +
                               Command.cameraTargets[0];
                for (var i = 1; i < Command.cameraPositions.Count; i++)
                {
                    viewList += "," + Command.cameraNames[i] + "," + Command.cameraPositions[i] + "," +
                                Command.cameraTargets[i];
                }
                _viewsAndLayersDict.Add("views", viewList);
            }

            _container.Object.UserData = _viewsAndLayersDict;


            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            };

            myjs = JsonConvert.SerializeObject(_container, Formatting.None, settings);

            File.WriteAllText(_filename, myjs);
        }

        public void OnPolymesh(PolymeshTopology polymesh)
        {
            var pts = polymesh.GetPoints();
            var t = CurrentTransform;

            pts = pts.Select(p => t.OfPoint(p)).ToList();

            foreach (var facet in polymesh.GetFacets())
            {
                var v1 = CurrentVerticesPerMaterial.AddVertex(new PointInt(pts[facet.V1], _switchCoordinates));
                var v2 = CurrentVerticesPerMaterial.AddVertex(new PointInt(pts[facet.V2], _switchCoordinates));
                var v3 = CurrentVerticesPerMaterial.AddVertex(new PointInt(pts[facet.V3], _switchCoordinates));

                CurrentGeometryPerMaterial.Data.Faces.Add(0);
                CurrentGeometryPerMaterial.Data.Faces.Add(v1);
                CurrentGeometryPerMaterial.Data.Faces.Add(v2);
                CurrentGeometryPerMaterial.Data.Faces.Add(v3);
            }
        }

        public void OnMaterial(MaterialNode node)
        {
            //Debug.WriteLine( "     --> On Material: " 
            //  + node.MaterialId + ": " + node.NodeName );

            // OnMaterial method can be invoked for every 
            // single out-coming mesh even when the material 
            // has not actually changed. Thus it is usually
            // beneficial to store the current material and 
            // only get its attributes when the material 
            // actually changes.

            var id = node.MaterialId;
            if (ElementId.InvalidElementId != id)
            {
                if (_currentDoc == null)
                    return;

                var m = _currentDoc.GetElement(node.MaterialId);
                SetCurrentMaterial(m.UniqueId);
            }
            else
            {
                //string uid = Guid.NewGuid().ToString();

                // Generate a GUID based on Color, 
                // transparency, etc. to avoid duplicating
                // non-element material definitions.

                var iColor = Util.ColorToInt(node.Color);
                var uid = $"MaterialNode_{iColor}_{Util.RealString(node.Transparency * 100)}";

                if (!_materials.ContainsKey(uid))
                {
                    var m = new Va3cContainer.Va3cMaterial
                    {
                        UUID = uid,
                        Type = "MeshLambertMaterial",
                        Color = iColor
                    };

                    m.Ambient = m.Color;
                    m.Emissive = 0;
                    m.Shading = 1;
                    m.Opacity = 1; // 128 - material.Transparency;
                    m.Opacity =
                        1.0 - node
                            .Transparency; // Revit MaterialNode has double Transparency in ?range?, three.js expects Opacity in [0.0,1.0]
                    m.Transparent = 0.0 < node.Transparency;
                    m.Wireframe = false;

                    _materials.Add(uid, m);
                }
                SetCurrentMaterial(uid);
            }
        }

        public bool IsCanceled()
        {
            // This method is invoked many 
            // times during the export process.

            return false;
        }

        // Removed in Revit 2017:
        //public void OnDaylightPortal( DaylightPortalNode node )
        //{
        //  Debug.WriteLine( "OnDaylightPortal: " + node.NodeName );
        //  Asset asset = node.GetAsset();
        //  Debug.WriteLine( "OnDaylightPortal: Asset:"
        //    + ( ( asset != null ) ? asset.Name : "Null" ) );
        //}

        public void OnRPC(RPCNode node)
        {
            Debug.WriteLine("OnRPC: " + node.NodeName);
            //Asset asset = node.GetAsset();
            //Debug.WriteLine("OnRPC: Asset:"
            //  + ((asset != null) ? asset.Name : "Null"));
        }

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            Debug.WriteLine("OnViewBegin: "
                            + node.NodeName + "(" + node.ViewId.IntegerValue
                            + "): LOD: " + node.LevelOfDetail);

            return RenderNodeAction.Proceed;
        }

        public void OnViewEnd(ElementId elementId)
        {
            Debug.WriteLine("OnViewEnd: Id: " + elementId.IntegerValue);
            // Note: This method is invoked even for a view that was skipped.
        }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            var e = _currentDoc.GetElement(elementId);

            // note: because of links and that the linked models might have had the same template, we need to 
            // make this further unique...
            var uid = e.UniqueId + "_" + _currentDoc.Title;

            Debug.WriteLine($"OnElementBegin: id {elementId.IntegerValue} category {e.Category.Name} name {e.Name}");

            if (_objects.ContainsKey(uid))
            {
                Debug.WriteLine("\r\n*** Duplicate element!\r\n");
                return RenderNodeAction.Skip;
            }

            if (null == e.Category)
            {
                Debug.WriteLine("\r\n*** Non-category element!\r\n");
                return RenderNodeAction.Skip;
            }

            _elementStack.Push(elementId);

            var idsMaterialGeometry = e.GetMaterialIds(false);
            var n = idsMaterialGeometry.Count;

            if (1 < n)
            {
                Debug.Print("{0} has {1} materials: {2}",
                    Util.ElementDescription(e), n,
                    string.Join(", ", idsMaterialGeometry.Select(id => _currentDoc.GetElement(id).Name)));
            }

            // We handle a current element, which may either
            // be identical to the current object and have
            // one single current geometry or have 
            // multiple current child objects each with a 
            // separate current geometry.

            _currentElement = new Va3cContainer.Va3cObject();

            _currentElement.Name = Util.ElementDescription(e);
            _currentElement.Material = _currentMaterialUid;
            _currentElement.Matrix = new double[] {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
            _currentElement.Type = "RevitElement";
            _currentElement.UUID = uid;

            _currentObject = new Dictionary<string, Va3cContainer.Va3cObject>();
            _currentGeometry = new Dictionary<string, Va3cContainer.Va3cGeometry>();
            _vertices = new Dictionary<string, VertexLookupInt>();

            if (e.Category?.Material != null)
            {
                SetCurrentMaterial(e.Category.Material.UniqueId);
            }

            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId id)
        {
            // Note: this method is invoked even for 
            // elements that were skipped.

            var e = _currentDoc.GetElement(id);
            var uid = e.UniqueId;

            Debug.WriteLine($"OnElementEnd: id {id.IntegerValue} category {e.Category.Name} name {e.Name}");

            if (_elementStack.Contains(id) == false) return; // it was skipped?

            if (_objects.ContainsKey(uid))
            {
                Debug.WriteLine("\r\n*** Duplicate element!\r\n");
                return;
            }

            if (null == e.Category)
            {
                Debug.WriteLine("\r\n*** Non-category element!\r\n");
                return;
            }

            var materials = _vertices.Keys.ToList();
            var n = materials.Count;

            _currentElement.Children = new List<Va3cContainer.Va3cObject>(n);

            foreach (var material in materials)
            {
                var obj = _currentObject[material];
                var geo = _currentGeometry[material];

                foreach (var p in _vertices[material])
                {
                    geo.Data.Vertices.Add(_scale_vertex * p.Key.X);
                    geo.Data.Vertices.Add(_scale_vertex * p.Key.Y);
                    geo.Data.Vertices.Add(_scale_vertex * p.Key.Z);
                }
                obj.Geometry = geo.UUID;

                //QUESTION: Should we attempt to further ensure uniqueness? or should we just update the geometry that is there?
                //old: _geometries.Add(geo.uuid, geo);
                _geometries[geo.UUID] = geo;
                _currentElement.Children.Add(obj);
            }

            // var d = Util.GetElementProperties(e, true);
            // var d = Util.GetElementFilteredProperties(e, true); 
            var d = Util.GetElementProperties(e, true);
            var layerName = e.Category.Name;

            //add a property for layer
            d.Add("layer", layerName);



            if (!_viewsAndLayersDict.ContainsKey("layers")) _viewsAndLayersDict.Add("layers", layerName);
            else
            {
                if (!_layerList.Contains(layerName))
                {
                    _viewsAndLayersDict["layers"] += "," + layerName;
                }
            }

            if (!_layerList.Contains(layerName)) _layerList.Add(layerName);

            _currentElement.UserData = d;

            //also add guid to user data dictionary
            _currentElement.UserData.Add("revit_id", uid);

            _objects[_currentElement.UUID] = _currentElement;

            _elementStack.Pop();
        }

        public RenderNodeAction OnFaceBegin(FaceNode node)
        {
            // This method is invoked only if the 
            // custom exporter was set to include faces.

            // Debug.Assert(false, "we set exporter.IncludeFaces false");
            Debug.WriteLine("  OnFaceBegin: " + node.NodeName);
            return RenderNodeAction.Proceed;
        }

        public void OnFaceEnd(FaceNode node)
        {
            // This method is invoked only if the 
            // custom exporter was set to include faces.

            // Debug.Assert(false, "we set exporter.IncludeFaces false");
            Debug.WriteLine("  OnFaceEnd: " + node.NodeName);
            // Note: This method is invoked even for faces that were skipped.
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            Debug.WriteLine("  OnInstanceBegin: " + node.NodeName
                            + " symbol: " + node.GetSymbolId().IntegerValue);
            // This method marks the start of processing a family instance
            _transformationStack.Push(CurrentTransform.Multiply(node.GetTransform()));

            // We can either skip this instance or proceed with rendering it.
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            Debug.WriteLine("  OnInstanceEnd: " + node.NodeName);
            // Note: This method is invoked even for instances that were skipped.
            _transformationStack.Pop();
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            Debug.WriteLine("  OnLinkBegin: " + node.NodeName + " Document: " + node.GetDocument().Title + ": Id: " +
                            node.GetSymbolId().IntegerValue);
            _currentDoc = node.GetDocument();
            _transformationStack.Push(CurrentTransform.Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            Debug.WriteLine("  OnLinkEnd: " + node.NodeName);
            // reset for the original document
            _currentDoc = _doc;

            // Note: This method is invoked even for instances that were skipped.
            _transformationStack.Pop();
        }

        public void OnLight(LightNode node)
        {
            Debug.WriteLine("OnLight: " + node.NodeName);
            //Asset asset = node.GetAsset();
            //Debug.WriteLine("OnLight: Asset:" + ((asset != null) ? asset.Name : "Null"));
        }
    }
}
