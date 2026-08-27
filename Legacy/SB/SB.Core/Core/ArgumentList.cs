namespace SB.Core
{
    public interface IArgumentList
    {
        public void Merge(IArgumentList Another);
        public IArgumentList Copy();
    }

    public class ArgumentList<T> : SortedSet<T>, IArgumentList
    {
        public void Merge(IArgumentList? Another)
        {
            if (Another is not ArgumentList<T>)
                throw new ArgumentException("ArgumentList type mismatch!");
            var ToMerge = Another as ArgumentList<T>;
            if (ToMerge is not null)
            {
                this.AddRange(ToMerge!);
            }
        }

        public void Substract(IArgumentList? Another)
        {
            if (Another is not ArgumentList<T>)
                throw new ArgumentException("ArgumentList type mismatch!");
            var ToSubstract = Another as ArgumentList<T>;
            if (ToSubstract is not null)
            {
                this.ExceptWith(ToSubstract!);
            }
        }

        public IArgumentList Copy()
        {
            IArgumentList Cpy = new ArgumentList<T>();
            Cpy.Merge(this);
            return Cpy;
        }
    }
}
