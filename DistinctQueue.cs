using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BuildSmarterContentPackage
{
    class DistinctQueue<T>
    {
        HashSet<T> m_hasSeen = new HashSet<T>();
        Queue<T> m_queue = new Queue<T>();
        int m_dequeued = 0;

        public bool Enqueue(T value)
        {
            if (!m_hasSeen.Add(value))
            {
                return false;
            }
            m_queue.Enqueue(value);
            return true;
        }

        public T Dequeue()
        {
            var value = m_queue.Dequeue();
            ++m_dequeued;
            return value;
        }

        public T Peek()
        {
            return m_queue.Peek();
        }

        public int Count
        {
            get { return m_queue.Count(); }
        }

        public int CountDistinct
        {
            get { return m_hasSeen.Count; }
        }

        public int CountDequeued
        {
            get { return m_dequeued; }
        }

        public int Load(IEnumerator<T> en)
        {
            int n = 0;
            while (en.MoveNext())
            {
                Enqueue(en.Current);
                ++n;
            }
            return n;
        }

    }
}
