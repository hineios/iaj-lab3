﻿using Assets.Scripts.IAJ.Unity.Pathfinding.Heuristics;
using RAIN.Navigation.Graph;
using RAIN.Navigation.NavMesh;
using UnityEngine;

namespace Assets.Scripts.IAJ.Unity.Pathfinding
{
    public class AStarPathfinding
    {
        public NavMeshPathGraph NavMeshGraph { get; protected set; }
        //how many nodes do we process on each call to the search method
        public uint NodesPerSearch { get; set; }

        public uint TotalProcessedNodes { get; protected set; }
        public int MaxOpenNodes { get; protected set; }
        public float TotalProcessingTime { get; protected set; }
        public bool InProgress { get; protected set; }

        public IOpenSet Open { get; protected set; }
        public IClosedSet Closed { get; protected set; }

        public NavigationGraphNode GoalNode { get; protected set; }
        public NavigationGraphNode StartNode { get; protected set; }
        public Vector3 StartPosition { get; protected set; }
        public Vector3 GoalPosition { get; protected set; }

        //heuristic function
        public IHeuristic Heuristic { get; protected set; }

        public AStarPathfinding(NavMeshPathGraph graph, IOpenSet open, IClosedSet closed, IHeuristic heuristic)
        {
            this.NavMeshGraph = graph;
            this.Open = open;
            this.Closed = closed;
            this.NodesPerSearch = uint.MaxValue; //by default we process all nodes in a single request
            this.InProgress = false;
            this.Heuristic = heuristic;
        }

        public void InitializePathfindingSearch(Vector3 startPosition, Vector3 goalPosition)
        {
            this.StartPosition = startPosition;
            this.GoalPosition = goalPosition;
            this.StartNode = this.Quantize(this.StartPosition);
            this.GoalNode = this.Quantize(this.GoalPosition);

            //if it is not possible to quantize the positions and find the corresponding nodes, then we cannot proceed
            if (this.StartNode == null || this.GoalNode == null) return;

            //I need to do this because in Recast NavMesh graph, the edges of polygons are considered to be nodes and not the connections.
            //Theoretically the Quantize method should then return the appropriate edge, but instead it returns a polygon
            //Therefore, we need to create one explicit connection between the polygon and each edge of the corresponding polygon for the search algorithm to work
            ((NavMeshPoly)this.StartNode).AddConnectedPoly(this.StartPosition);
            ((NavMeshPoly)this.GoalNode).AddConnectedPoly(this.GoalPosition);

            this.InProgress = true;
            this.TotalProcessedNodes = 0;
            this.TotalProcessingTime = 0.0f;
            this.MaxOpenNodes = 0;

            var initialNode = new NodeRecord
            {
                gValue = 0,
                hValue = this.Heuristic.H(this.StartNode, this.GoalNode),
                node = this.StartNode
            };

            initialNode.fValue = AStarPathfinding.F(initialNode);

            this.Open.Initialize(); 
            this.Open.Add(initialNode);
            this.Closed.Initialize();
        }

        protected virtual void ProcessChildNode(NodeRecord bestNode, NavigationGraphEdge connectionEdge)
        {
            //this is where you process a child node 
            NodeRecord childNode = GenerateChildNodeRecord(bestNode, connectionEdge);
            NodeRecord openNode = Open.Search(childNode);
            NodeRecord closedNode = Closed.Search(childNode);
            bool inOpen = openNode != null ? true : false;
            bool inClosed = closedNode != null ? true : false;
            if (!inOpen && !inClosed) {
                Open.Add(childNode);
            }
            else if (inOpen && childNode.fValue < openNode.fValue) {
                Open.Remove(openNode);
                Open.Add(childNode);
            } else if (inClosed && childNode.fValue < closedNode.fValue) {
                Closed.Remove(closedNode);
                Open.Add(childNode);
            }
        }


        //this method should return true if the Search process finished (i.e either because it found a solution or because there was no solution
        //it should return false if the search process didn't finish yet
        //when returning true, the solution parameter should be set to null if there is no solution. Otherwise it must contain the found solution
        //if the parameter returnPartialSolution is true, then the user wants to have a partial path to the best node so far even when the search has not finished searching
        public bool Search(out Path solution, bool returnPartialSolution = false)
        {
            int counter = 0;
			float startTime = Time.realtimeSinceStartup;
            while(true)
            {
                if (Open.Count() == 0)
                {
                    solution = null;
					TotalProcessingTime += Time.realtimeSinceStartup - startTime;
					CleanUp ();
                    return true;
                }
                NodeRecord bestNode = Open.GetBestAndRemove();
                if (bestNode.node.Equals(GoalNode))
                {
                    solution = CalculateSolution(bestNode, false);
					this.InProgress = false;
					TotalProcessingTime += Time.realtimeSinceStartup - startTime;
					CleanUp ();
                    return true;
                }
                Closed.Add(bestNode);
                //to determine the connections of the selected nodeRecord you need to look at the NavigationGraphNode' EdgeOut  list
                //something like this
                var outConnections = bestNode.node.OutEdgeCount;
                for (int i = 0; i < outConnections; i++)
                {
                    this.ProcessChildNode(bestNode, bestNode.node.EdgeOut(i));
                }
                if (counter >= NodesPerSearch)
                {
					TotalProcessingTime += Time.realtimeSinceStartup - startTime;
                    solution = CalculateSolution(bestNode, true);
                    return false;
                }
                counter += 1;
                TotalProcessedNodes += 1;
                if (MaxOpenNodes <= Open.Count()) MaxOpenNodes = Open.Count(); 
            }
        }                 

        private NavigationGraphNode Quantize(Vector3 position)
        {
            return this.NavMeshGraph.QuantizeToNode(position, 1.0f);
        }

		//method used to clean up the extra nodes that were added to the graph in the initialization process
		//it should be used when the search finds a solution or when the search has finished and there is no solution
        private void CleanUp()
        {
            //I need to remove the connections created in the initialization process
            if (this.StartNode != null)
            {
                ((NavMeshPoly)this.StartNode).RemoveConnectedPoly();
            }

            if (this.GoalNode != null)
            {
                ((NavMeshPoly)this.GoalNode).RemoveConnectedPoly();    
            }
        }

        private NodeRecord GenerateChildNodeRecord(NodeRecord parent, NavigationGraphEdge connectionEdge)
        {
            var childNode = connectionEdge.ToNode;
            var childNodeRecord = new NodeRecord
            {
                node = childNode,
                parent = parent,
                gValue = parent.gValue + connectionEdge.Cost,
                hValue = this.Heuristic.H(childNode, this.GoalNode)
            };

            childNodeRecord.fValue = F(childNodeRecord);

            return childNodeRecord;
        }

        private Path CalculateSolution(NodeRecord node, bool partial)
        {
            var path = new Path
            {
                IsPartial = partial
            };
            var currentNode = node;

            path.PathPositions.Add(this.GoalPosition);

            //I need to remove the first Node and the last Node because they correspond to the dummy first and last Polygons that were created by the initialization.
            //And for instance I don't want to be forced to go to the center of the initial polygon before starting to move towards my destination.

            //skip the last node, but only if the solution is not partial (if the solution is partial, the last node does not correspond to the dummy goal polygon)
            if (!partial && currentNode.parent != null)
            {
                currentNode = currentNode.parent;
            }
            
            while (currentNode.parent != null)
            {
                path.PathNodes.Add(currentNode.node); //we need to reverse the list because this operator add elements to the end of the list
                path.PathPositions.Add(currentNode.node.LocalPosition);

                if (currentNode.parent.parent == null) break; //this skips the first node
                currentNode = currentNode.parent;
            }

            path.PathNodes.Reverse();
            path.PathPositions.Reverse();
            return path;
        }

        private static float F(NodeRecord node)
        {
            return node.gValue + node.hValue;
        }

    }
}
